using System;
using System.Collections.Generic;
using System.IO;

namespace MusicBeePlugin
{
    internal sealed class UploadedFileCache
    {
        private readonly object _sync = new object();
        private readonly string _filePath;
        private Dictionary<string, string> _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public UploadedFileCache(string storageFolder)
        {
            if (string.IsNullOrWhiteSpace(storageFolder))
            {
                throw new ArgumentException("Storage folder is required.", nameof(storageFolder));
            }

            Directory.CreateDirectory(storageFolder);
            _filePath = Path.Combine(storageFolder, "misskey_uploaded_files.cache");
            Load();
        }

        public string Get(string trackKey)
        {
            if (string.IsNullOrWhiteSpace(trackKey))
            {
                return null;
            }

            lock (_sync)
            {
                return _entries.TryGetValue(trackKey, out var value) ? value : null;
            }
        }

        public void Set(string trackKey, string fileId)
        {
            if (string.IsNullOrWhiteSpace(trackKey) || string.IsNullOrWhiteSpace(fileId))
            {
                return;
            }

            lock (_sync)
            {
                if (_entries.TryGetValue(trackKey, out var existing) && string.Equals(existing, fileId, StringComparison.Ordinal))
                {
                    return;
                }

                _entries[trackKey] = fileId;
                Save();
            }
        }

        private void Load()
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_filePath))
            {
                _entries = entries;
                return;
            }

            foreach (var rawLine in File.ReadAllLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var line = rawLine.Trim();
                var parts = line.Split(new[] { '|' }, 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                entries[parts[0]] = parts[1];
            }

            _entries = entries;
        }

        private void Save()
        {
            var lines = new List<string>(_entries.Count);
            foreach (var pair in _entries)
            {
                lines.Add(pair.Key + "|" + pair.Value);
            }

            File.WriteAllLines(_filePath, lines);
        }
    }
}
