using System;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private const string PluginStorageFolder = "NowPlayingForMisskey";
        private const int CompletionToleranceMilliseconds = 5000;

        private readonly object _stateLock = new object();
        private readonly object _logLock = new object();
        private readonly HttpClient _httpClient = new HttpClient();

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private PluginSettings _settings = new PluginSettings();
        private TrackSnapshot _currentTrack;
        private string _storageFolder = string.Empty;
        private int _tracksSinceLastPost;
        private CancellationTokenSource _postCancellationSource = new CancellationTokenSource();
        private UploadedFileCache _uploadedFileCache;
        private string _logFilePath;

        static Plugin()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "NowPlaying for Misskey";
            about.Description = "Post finished tracks to Misskey.";
            about.Author = "天波たくみ（amanami-takumi) / misskey.seitendan.com/@takumin3211";
            about.TargetApplication = string.Empty;
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 1;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;

            var persistentPath = mbApiInterface.Setting_GetPersistentStoragePath();
            if (!string.IsNullOrWhiteSpace(persistentPath))
            {
                _storageFolder = Path.Combine(persistentPath, PluginStorageFolder);
                try
                {
                    Directory.CreateDirectory(_storageFolder);
                    InitializeLogFile();
                }
                catch (Exception ex)
                {
                    AppendLogLineDirect("ログファイルの初期化に失敗しました: " + ex.Message);
                }

                try
                {
                    _uploadedFileCache = new UploadedFileCache(_storageFolder);
                }
                catch (Exception ex)
                {
                    Trace("ファイルキャッシュの初期化に失敗しました: " + ex.Message);
                }
            }
            else
            {
                _storageFolder = string.Empty;
            }
            _settings = PluginSettings.Load(_storageFolder);
            _settings.InstanceUrl = NormalizeInstanceUrl(_settings.InstanceUrl);
            _tracksSinceLastPost = 0;
            _currentTrack = CaptureCurrentTrackSnapshot();

            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            using (var dialog = new MisskeySettingsDialog(_settings?.Clone() ?? new PluginSettings()))
            {
                if (dialog.ShowDialog() == DialogResult.OK && dialog.ResultSettings != null)
                {
                    _settings = dialog.ResultSettings;
                    _settings.InstanceUrl = NormalizeInstanceUrl(_settings.InstanceUrl);
                    SaveSettings();
                    _tracksSinceLastPost = 0;
                    return true;
                }
            }

            return false;
        }

        public void SaveSettings()
        {
            try
            {
                _settings?.Save(_storageFolder);
            }
            catch (Exception ex)
            {
                Trace("設定の保存に失敗しました: " + ex.Message);
            }
        }

        public void Close(PluginCloseReason reason)
        {
            try
            {
                _postCancellationSource.Cancel();
                _postCancellationSource.Dispose();
                _httpClient.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        public void Uninstall()
        {
            try
            {
                if (!string.IsNullOrEmpty(_storageFolder) && Directory.Exists(_storageFolder))
                {
                    Directory.Delete(_storageFolder, true);
                }
            }
            catch (Exception ex)
            {
                Trace("設定の削除に失敗しました: " + ex.Message);
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            try
            {
                switch (type)
                {
                    case NotificationType.PluginStartup:
                        lock (_stateLock)
                        {
                            _currentTrack = CaptureCurrentTrackSnapshot();
                        }

                        break;

                    case NotificationType.TrackChanged:
                        lock (_stateLock)
                        {
                            _currentTrack = CaptureCurrentTrackSnapshot();
                        }

                        break;

                    case NotificationType.TrackChanging:
                        HandleTrackCompletionAttempt(TrackCompletionTrigger.TrackChanging);
                        break;

                    case NotificationType.PlayStateChanged:
                        if (mbApiInterface.Player_GetPlayState() == PlayState.Stopped)
                        {
                            HandleTrackCompletionAttempt(TrackCompletionTrigger.PlayStateStopped);
                        }

                        break;

                    case NotificationType.NowPlayingListEnded:
                        HandleTrackCompletionAttempt(TrackCompletionTrigger.PlaylistEnded);
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace("通知の送信に失敗しました: " + ex.Message);
            }
        }

        public string[] GetProviders()
        {
            return null;
        }

        public string RetrieveLyrics(string sourceFileUrl, string artist, string trackTitle, string album, bool synchronisedPreferred, string provider)
        {
            return null;
        }

        public string RetrieveArtwork(string sourceFileUrl, string albumArtist, string album, string provider)
        {
            return null;
        }

        private TrackSnapshot CaptureCurrentTrackSnapshot()
        {
            try
            {
                var state = mbApiInterface.Player_GetPlayState();
                if (state != PlayState.Playing && state != PlayState.Paused)
                {
                    return null;
                }

                var fileUrl = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Url);
                if (string.IsNullOrWhiteSpace(fileUrl))
                {
                    return null;
                }

                var title = SafeValue(mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle), "Unknown Title");
                var album = SafeValue(mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album), "Unknown Album");
                var artist = SafeValue(mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist), "Unknown Artist");
                var duration = mbApiInterface.NowPlaying_GetDuration();

                return new TrackSnapshot(fileUrl, title, album, artist, duration);
            }
            catch (Exception ex)
            {
                Trace("楽曲情報の取得に失敗しました: " + ex.Message);
                return null;
            }
        }

        private void HandleTrackCompletionAttempt(TrackCompletionTrigger trigger)
        {
            TrackSnapshot finishedTrack;
            lock (_stateLock)
            {
                finishedTrack = _currentTrack;
            }

            if (finishedTrack == null)
            {
                return;
            }

            var position = TryGetPlayerPosition();
            if (!HasTrackFinished(finishedTrack, trigger, position))
            {
                if (trigger == TrackCompletionTrigger.TrackChanging)
                {
                    lock (_stateLock)
                    {
                        _currentTrack = null;
                    }
                }

                return;
            }

            lock (_stateLock)
            {
                _currentTrack = null;
            }

            HandleTrackFinished(finishedTrack);
        }

        private void HandleTrackFinished(TrackSnapshot track)
        {
            if (track == null || _settings == null || !_settings.IsConfigured)
            {
                return;
            }

            var frequency = Math.Max(1, _settings.PostEvery);
            _tracksSinceLastPost++;
            if (_tracksSinceLastPost < frequency)
            {
                return;
            }

            _tracksSinceLastPost = 0;
            _ = Task.Run(() => PostToMisskeyAsync(track, _postCancellationSource.Token));
        }

        private async Task PostToMisskeyAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            try
            {
                Trace($"Misskey投稿を準備します: {track?.Title ?? "(title missing)"} / {track?.Artist ?? "(artist missing)"}");
                var endpoint = BuildEndpointUrl(_settings.InstanceUrl);
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    Trace("Misskey投稿を中止しました: インスタンスURLが未設定です。");
                    return;
                }

                var noteText = BuildNoteText(track, _settings.CustomHashtags);
                var fileId = await TryGetOrUploadAlbumArtAsync(track, cancellationToken).ConfigureAwait(false);

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
                request.Content = new StringContent(BuildPayload(noteText, fileId), Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Trace($"Misskey投稿に失敗しました: {(int)response.StatusCode} {response.ReasonPhrase} 応答={Shorten(responseBody, 500)}");
                    }
                    else
                    {
                        Trace($"Misskey投稿が成功しました (fileId={(fileId ?? "添付なし")}).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                Trace("Misskey投稿中にエラーが発生しました: " + ex.Message);
            }
        }




        private static string BuildEndpointUrl(string baseUrl)
        {
            var normalized = NormalizeInstanceUrl(baseUrl);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            return normalized + "/api/notes/create";
        }

        private static string BuildDriveEndpointUrl(string baseUrl)
        {
            var normalized = NormalizeInstanceUrl(baseUrl);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            return normalized + "/api/drive/files/create";
        }

        private static string BuildPayload(string noteText, string fileId)
        {
            var builder = new StringBuilder();
            builder.Append("{\"visibility\":\"public\"");
            builder.Append(",\"text\":");
            builder.Append(ToJsonString(noteText));
            if (!string.IsNullOrEmpty(fileId))
            {
                builder.Append(",\"fileIds\":[");
                builder.Append(ToJsonString(fileId));
                builder.Append(']');
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string BuildNoteText(TrackSnapshot track, string customHashtags)
        {
            if (track == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append(track.Title);
            builder.Append(" / ");
            builder.Append(track.Artist);
            builder.AppendLine();
            builder.Append('（');
            builder.Append("Album：");
            builder.Append(track.Album);
            builder.AppendLine(")");
            builder.Append("#NowPlaying #MusicBee");

            var trimmedTags = string.IsNullOrWhiteSpace(customHashtags) ? string.Empty : customHashtags.Trim();
            if (!string.IsNullOrEmpty(trimmedTags))
            {
                builder.Append(' ');
                builder.Append(trimmedTags);
            }

            return builder.ToString();
        }

        private async Task<string> TryGetOrUploadAlbumArtAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            if (track == null)
            {
                Trace("アルバムアート添付をスキップ: トラック情報が null です。");
                return null;
            }

            if (_settings == null)
            {
                Trace("アルバムアート添付をスキップ: 設定が初期化されていません。");
                return null;
            }

            if (!_settings.AttachAlbumArt)
            {
                Trace($"アルバムアート添付をスキップ: 設定で無効です ({track.Title}).");
                return null;
            }

            var cacheKey = BuildTrackCacheKey(track);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                var cached = _uploadedFileCache?.Get(cacheKey);
                if (!string.IsNullOrEmpty(cached))
                {
                    Trace($"アルバムアート添付: キャッシュ済みファイルID {cached} を再利用します ({track.Title}).");
                    return cached;
                }
            }

            var artwork = TryGetAlbumArtwork(track);
            if (artwork == null)
            {
                Trace($"アルバムアートを取得できませんでした ({track.Title}).");
                return null;
            }

            var endpoint = BuildDriveEndpointUrl(_settings.InstanceUrl);
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                Trace("アルバムアートのアップロードを中止しました: インスタンスURLが未設定です。");
                return null;
            }

            Trace($"アルバムアートをアップロードします: {artwork.FileName} ({track.Title}).");

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);

                using (var content = new MultipartFormDataContent())
                {
                    content.Add(new StringContent("false"), "isSensitive");
                    content.Add(new StringContent("false"), "force");

                    var fileContent = new ByteArrayContent(artwork.Data);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(artwork.ContentType);
                    content.Add(fileContent, "file", artwork.FileName);

                    request.Content = content;

                    using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Trace($"Misskeyアルバムアートのアップロードに失敗しました: {(int)response.StatusCode} {response.ReasonPhrase} 応答={Shorten(responseBody, 500)}");
                            return null;
                        }

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var fileId = ExtractJsonString(body, "id");
                        if (string.IsNullOrEmpty(fileId))
                        {
                            Trace("アップロード応答からファイルIDを取得できませんでした。");
                            return null;
                        }

                        if (!string.IsNullOrEmpty(cacheKey))
                        {
                            _uploadedFileCache?.Set(cacheKey, fileId);
                            Trace($"アルバムアートのファイルIDをキャッシュしました: key={cacheKey}");
                        }

                        Trace($"アルバムアートのアップロードに成功しました。fileId={fileId}");
                        return fileId;
                    }
                }
            }
        }

        private ArtworkData TryGetAlbumArtwork(TrackSnapshot track)
        {
            if (track == null)
            {
                Trace("アルバムアートの取得をスキップ: トラック情報が null です。");
                return null;
            }

            if (string.IsNullOrWhiteSpace(track.FileUrl))
            {
                Trace($"アルバムアートの取得をスキップ: FileUrl が空です ({track.Title}).");
                return null;
            }

            if (mbApiInterface.Library_GetArtworkEx == null)
            {
                Trace("アルバムアートの取得をスキップ: Library_GetArtworkEx が利用できません。");
                return null;
            }

            try
            {
                byte[] imageData = null;
                string pictureUrl = null;
                PictureLocations pictureLocations = PictureLocations.None;
                var success = mbApiInterface.Library_GetArtworkEx(track.FileUrl, 0, true, ref pictureLocations, ref pictureUrl, ref imageData);
                if (!success || imageData == null || imageData.Length == 0)
                {
                    if (!string.IsNullOrEmpty(pictureUrl) && File.Exists(pictureUrl))
                    {
                        Trace($"アルバムアートをファイルから読み込みます: {pictureUrl}");
                        imageData = File.ReadAllBytes(pictureUrl);
                    }
                    else
                    {
                        Trace("埋め込みアルバムアートが見つからず、ファイルにもアクセスできませんでした。");
                        return null;
                    }
                }

                var extension = DetermineImageExtension(imageData, out var contentType);
                var fileName = BuildArtworkFileName(track, extension);
                Trace($"アルバムアートを取得しました: {fileName} ({imageData?.Length ?? 0} bytes).");
                return new ArtworkData(fileName, contentType, imageData);
            }
            catch (Exception ex)
            {
                Trace("アルバムアートの取得に失敗しました: " + ex.Message);
                return null;
            }
        }

        private static string DetermineImageExtension(byte[] data, out string mimeType)
        {
            if (data != null && data.Length >= 4)
            {
                if (data[0] == 0xFF && data[1] == 0xD8)
                {
                    mimeType = "image/jpeg";
                    return ".jpg";
                }

                if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                {
                    mimeType = "image/png";
                    return ".png";
                }

                if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
                {
                    mimeType = "image/gif";
                    return ".gif";
                }

                if (data[0] == 0x42 && data[1] == 0x4D)
                {
                    mimeType = "image/bmp";
                    return ".bmp";
                }
            }

            mimeType = "application/octet-stream";
            return ".bin";
        }

        private static string BuildArtworkFileName(TrackSnapshot track, string extension)
        {
            var title = track?.Title ?? "artwork";
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalid, '_');
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = "artwork";
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            return title + extension;
        }

        private static string BuildTrackCacheKey(TrackSnapshot track)
        {
            return string.IsNullOrWhiteSpace(track?.FileUrl) ? null : track.FileUrl;
        }

        private static string ExtractJsonString(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var pattern = $"\"{propertyName}\"";
            var index = json.IndexOf(pattern, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            index = json.IndexOf(':', index + pattern.Length);
            if (index < 0)
            {
                return null;
            }

            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            if (index >= json.Length || json[index] != '\"')
            {
                return null;
            }

            index++;
            var builder = new StringBuilder();
            var escaped = false;

            while (index < json.Length)
            {
                var ch = json[index++];
                if (escaped)
                {
                    switch (ch)
                    {
                        case '\"':
                        case '\\':
                        case '/':
                            builder.Append(ch);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (index + 3 < json.Length)
                            {
                                var hex = json.Substring(index, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                                {
                                    builder.Append((char)codePoint);
                                }

                                index += 4;
                            }

                            break;
                        default:
                            builder.Append(ch);
                            break;
                    }

                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '\"')
                {
                    break;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private void InitializeLogFile()
        {
            if (string.IsNullOrWhiteSpace(_storageFolder))
            {
                _logFilePath = null;
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logFilePath = Path.Combine(_storageFolder, $"log_{timestamp}.txt");
            AppendLogLineDirect("ログファイルを初期化しました。");
        }

        private void AppendLogLineDirect(string message)
        {
            if (message == null)
            {
                message = string.Empty;
            }

            if (string.IsNullOrEmpty(_logFilePath))
            {
                return;
            }

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                lock (_logLock)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // ignored
            }
        }

        private static string ToJsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 4);
            sb.Append('\"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            sb.Append('\"');
            return sb.ToString();
        }

        private bool HasTrackFinished(TrackSnapshot track, TrackCompletionTrigger trigger, int? positionMilliseconds)
        {
            if (track == null)
            {
                return false;
            }

            if (trigger == TrackCompletionTrigger.PlaylistEnded)
            {
                return true;
            }

            if (!positionMilliseconds.HasValue || track.DurationMilliseconds <= 0)
            {
                return false;
            }

            return positionMilliseconds.Value >= Math.Max(0, track.DurationMilliseconds - CompletionToleranceMilliseconds);
        }

        private int? TryGetPlayerPosition()
        {
            try
            {
                return mbApiInterface.Player_GetPosition();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string NormalizeInstanceUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim().TrimEnd('/');
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            return trimmed;
        }

        private void Trace(string message)
        {
            AppendLogLineDirect(message);
            try
            {
                if (mbApiInterface.MB_Trace != null)
                {
                    mbApiInterface.MB_Trace("[NowPlaying Misskey] " + message);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    internal enum TrackCompletionTrigger
    {
        TrackChanging,
        PlayStateStopped,
        PlaylistEnded
    }

    internal sealed class TrackSnapshot
    {
        public TrackSnapshot(string fileUrl, string title, string album, string artist, int durationMilliseconds)
        {
            FileUrl = fileUrl ?? string.Empty;
            Title = string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title;
            Album = string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
            DurationMilliseconds = durationMilliseconds;
        }

        public string FileUrl { get; }
        public string Title { get; }
        public string Album { get; }
        public string Artist { get; }
        public int DurationMilliseconds { get; }
    }

    internal sealed class ArtworkData
    {
        public ArtworkData(string fileName, string contentType, byte[] data)
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "artwork.bin" : fileName;
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
            Data = data ?? Array.Empty<byte>();
        }

        public string FileName { get; }
        public string ContentType { get; }
        public byte[] Data { get; }
    }
}



