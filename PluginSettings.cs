using System;
using System.IO;

namespace MusicBeePlugin
{
    internal sealed class PluginSettings
    {
        private const string SettingsFileName = "misskey_settings.ini";

        public string InstanceUrl { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public int PostEvery { get; set; } = 1;
        public string CustomHashtags { get; set; } = string.Empty;
        public bool AttachAlbumArt { get; set; } = true;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(InstanceUrl) &&
            !string.IsNullOrWhiteSpace(AccessToken);

        public PluginSettings Clone()
        {
            return new PluginSettings
            {
                InstanceUrl = InstanceUrl,
                AccessToken = AccessToken,
                PostEvery = PostEvery,
                CustomHashtags = CustomHashtags,
                AttachAlbumArt = AttachAlbumArt
            };
        }

        public static PluginSettings Load(string storageFolder)
        {
            var settings = new PluginSettings();
            if (string.IsNullOrWhiteSpace(storageFolder))
            {
                return settings;
            }

            var filePath = GetFilePath(storageFolder);
            if (!File.Exists(filePath))
            {
                return settings;
            }

            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var key = parts[0].Trim();
                var value = parts[1];

                switch (key)
                {
                    case "InstanceUrl":
                        settings.InstanceUrl = value?.Trim() ?? string.Empty;
                        break;
                    case "AccessToken":
                        settings.AccessToken = value?.Trim() ?? string.Empty;
                        break;
                    case "PostEvery":
                        if (int.TryParse(value, out var parsed) && parsed > 0)
                        {
                            settings.PostEvery = parsed;
                        }
                        break;
                    case "CustomHashtags":
                        settings.CustomHashtags = value?.Trim() ?? string.Empty;
                        break;
                    case "AttachAlbumArt":
                        if (bool.TryParse(value, out var boolParsed))
                        {
                            settings.AttachAlbumArt = boolParsed;
                        }
                        else if (int.TryParse(value, out var intBool))
                        {
                            settings.AttachAlbumArt = intBool != 0;
                        }
                        break;
                }
            }

            settings.EnsureValid();
            return settings;
        }

        public void Save(string storageFolder)
        {
            if (string.IsNullOrWhiteSpace(storageFolder))
            {
                throw new ArgumentException("Storage folder is required.", nameof(storageFolder));
            }

            Directory.CreateDirectory(storageFolder);
            EnsureValid();

            var filePath = GetFilePath(storageFolder);
            var contents = string.Join(
                Environment.NewLine,
                "InstanceUrl=" + InstanceUrl,
                "AccessToken=" + AccessToken,
                "PostEvery=" + PostEvery,
                "CustomHashtags=" + (CustomHashtags ?? string.Empty),
                "AttachAlbumArt=" + AttachAlbumArt);

            File.WriteAllText(filePath, contents);
        }

        private static string GetFilePath(string storageFolder)
        {
            return Path.Combine(storageFolder, SettingsFileName);
        }

        private void EnsureValid()
        {
            if (PostEvery < 1)
            {
                PostEvery = 1;
            }

            CustomHashtags = CustomHashtags?.Trim() ?? string.Empty;
        }
    }
}
