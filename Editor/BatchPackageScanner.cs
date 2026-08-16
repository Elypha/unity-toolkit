using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Elypha.UnityToolkit
{
    internal enum BatchPackageIssueSeverity
    {
        Conflict,
        Error,
    }

    internal sealed class BatchPackageIssue
    {
        public BatchPackageIssueSeverity Severity;
        public string AssetPath;
        public string Message;
    }

    internal sealed class BatchPackageChange
    {
        public string AssetPath;
        public bool AssetChanged;
        public bool MetaChanged;
        public string PreviousPackagePath;
    }

    internal sealed class BatchPackageReport
    {
        public string PackagePath;
        public int AssetCount;
        public int NewCount;
        public int UnchangedCount;
        public int DuplicateCount;
        public readonly List<BatchPackageChange> ProjectChanges = new List<BatchPackageChange>();
        public readonly List<BatchPackageChange> QueueChanges = new List<BatchPackageChange>();
        public readonly List<BatchPackageIssue> Issues = new List<BatchPackageIssue>();

        public int ProjectChangeCount => ProjectChanges.Count;
        public int QueueChangeCount => QueueChanges.Count;
        public int ChangeCount => ProjectChangeCount + QueueChangeCount;
        public int ConflictCount => Issues.Count(issue => issue.Severity == BatchPackageIssueSeverity.Conflict);
        public int ErrorCount => Issues.Count(issue => issue.Severity == BatchPackageIssueSeverity.Error);
    }

    internal sealed class BatchPackageScanReport
    {
        public readonly List<BatchPackageReport> Packages = new List<BatchPackageReport>();

        public int AssetCount => Packages.Sum(package => package.AssetCount);
        public int NewCount => Packages.Sum(package => package.NewCount);
        public int UnchangedCount => Packages.Sum(package => package.UnchangedCount);
        public int ProjectChangeCount => Packages.Sum(package => package.ProjectChangeCount);
        public int QueueChangeCount => Packages.Sum(package => package.QueueChangeCount);
        public int ChangeCount => Packages.Sum(package => package.ChangeCount);
        public int DuplicateCount => Packages.Sum(package => package.DuplicateCount);
        public int ConflictCount => Packages.Sum(package => package.ConflictCount);
        public int ErrorCount => Packages.Sum(package => package.ErrorCount);
    }

    internal static class BatchPackageScanner
    {
        private sealed class ExistingAsset
        {
            public bool AssetExists;
            public bool MetaExists;
            public bool IsDirectory;
            public string AssetHash;
            public string MetaHash;
            public string Guid;
        }

        public static BatchPackageScanReport Scan(IReadOnlyList<string> packagePaths)
        {
            var scan = new BatchPackageScanReport();
            var stagedByPath = new Dictionary<string, UnityPackageAsset>(StringComparer.OrdinalIgnoreCase);
            var stagedByGuid = new Dictionary<string, UnityPackageAsset>(StringComparer.OrdinalIgnoreCase);
            var existingCache = new Dictionary<string, ExistingAsset>(StringComparer.OrdinalIgnoreCase);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;

            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new InvalidOperationException("Cannot resolve the Unity project root.");
            }

            foreach (string packagePath in packagePaths)
            {
                var package = new BatchPackageReport { PackagePath = packagePath };
                scan.Packages.Add(package);

                try
                {
                    IReadOnlyList<UnityPackageAsset> assets = UnityPackageArchive.Read(packagePath);
                    package.AssetCount = assets.Count;

                    foreach (UnityPackageAsset asset in assets)
                    {
                        if (!TryNormaliseAssetPath(asset.Path, out string normalisedPath, out string pathError))
                        {
                            AddIssue(package, BatchPackageIssueSeverity.Error, asset.Path, pathError);
                            continue;
                        }

                        asset.Path = normalisedPath;
                        if (string.IsNullOrEmpty(asset.Guid))
                        {
                            AddIssue(package, BatchPackageIssueSeverity.Error, asset.Path, "The package entry has no valid GUID in asset.meta.");
                            continue;
                        }

                        CheckCurrentProject(package, asset, projectRoot, existingCache);
                        CheckEarlierPackages(package, asset, stagedByPath, stagedByGuid);

                        if (stagedByPath.TryGetValue(asset.Path, out UnityPackageAsset replaced) &&
                            !string.Equals(replaced.Guid, asset.Guid, StringComparison.OrdinalIgnoreCase) &&
                            stagedByGuid.TryGetValue(replaced.Guid, out UnityPackageAsset mapped) && ReferenceEquals(mapped, replaced))
                        {
                            stagedByGuid.Remove(replaced.Guid);
                        }

                        stagedByPath[asset.Path] = asset;
                        stagedByGuid[asset.Guid] = asset;
                    }
                }
                catch (Exception exception)
                {
                    AddIssue(package, BatchPackageIssueSeverity.Error, string.Empty, $"Cannot read package: {exception.Message}");
                }
            }

            return scan;
        }

        private static void CheckCurrentProject(BatchPackageReport package, UnityPackageAsset incoming, string projectRoot, IDictionary<string, ExistingAsset> cache)
        {
            if (!cache.TryGetValue(incoming.Path, out ExistingAsset existing))
            {
                existing = ReadExistingAsset(projectRoot, incoming.Path);
                cache[incoming.Path] = existing;
            }

            bool anythingAtPath = existing.AssetExists || existing.MetaExists;
            if (!anythingAtPath)
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(incoming.Guid);
                if (!string.IsNullOrEmpty(guidPath) && !string.Equals(guidPath, incoming.Path, StringComparison.OrdinalIgnoreCase))
                {
                    AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, $"GUID {incoming.Guid} already belongs to '{guidPath}' in this project.");
                }
                else
                {
                    package.NewCount++;
                }

                return;
            }

            if (!existing.AssetExists || !existing.MetaExists)
            {
                AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, "The project contains only the asset or only its .meta file.");
                return;
            }

            if (existing.IsDirectory != incoming.IsDirectory)
            {
                AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, "The package and project disagree on whether this path is a file or folder.");
                return;
            }

            if (!string.Equals(existing.Guid, incoming.Guid, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, $"Importing would replace project GUID {existing.Guid ?? "<missing>"} with {incoming.Guid}.");
                return;
            }

            bool assetMatches = incoming.IsDirectory || string.Equals(existing.AssetHash, incoming.AssetHash, StringComparison.Ordinal);
            bool metaMatches = string.Equals(existing.MetaHash, incoming.MetaHash, StringComparison.Ordinal);
            if (assetMatches && metaMatches)
            {
                package.UnchangedCount++;
                return;
            }

            package.ProjectChanges.Add(new BatchPackageChange
            {
                AssetPath = incoming.Path,
                AssetChanged = !assetMatches,
                MetaChanged = !metaMatches,
            });
        }

        private static void CheckEarlierPackages(BatchPackageReport package, UnityPackageAsset incoming, IDictionary<string, UnityPackageAsset> stagedByPath, IDictionary<string, UnityPackageAsset> stagedByGuid)
        {
            if (stagedByPath.TryGetValue(incoming.Path, out UnityPackageAsset previous))
            {
                if (incoming.ContentEquals(previous))
                {
                    package.DuplicateCount++;
                }
                else if (!string.Equals(previous.Guid, incoming.Guid, StringComparison.OrdinalIgnoreCase) || previous.IsDirectory != incoming.IsDirectory)
                {
                    AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, $"This package replaces the same path from '{Path.GetFileName(previous.PackagePath)}' with a different GUID or asset type.");
                }
                else
                {
                    package.QueueChanges.Add(new BatchPackageChange
                    {
                        AssetPath = incoming.Path,
                        AssetChanged = !string.Equals(incoming.AssetHash, previous.AssetHash, StringComparison.Ordinal),
                        MetaChanged = !string.Equals(incoming.MetaHash, previous.MetaHash, StringComparison.Ordinal),
                        PreviousPackagePath = previous.PackagePath,
                    });
                }
            }

            if (stagedByGuid.TryGetValue(incoming.Guid, out UnityPackageAsset sameGuid) && !string.Equals(sameGuid.Path, incoming.Path, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(package, BatchPackageIssueSeverity.Conflict, incoming.Path, $"GUID {incoming.Guid} is also assigned to '{sameGuid.Path}' by '{Path.GetFileName(sameGuid.PackagePath)}'.");
            }
        }

        private static ExistingAsset ReadExistingAsset(string projectRoot, string assetPath)
        {
            string fullPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            string metaPath = fullPath + ".meta";
            bool fileExists = File.Exists(fullPath);
            bool directoryExists = Directory.Exists(fullPath);
            bool metaExists = File.Exists(metaPath);

            return new ExistingAsset
            {
                AssetExists = fileExists || directoryExists,
                MetaExists = metaExists,
                IsDirectory = directoryExists,
                AssetHash = fileExists ? HashFile(fullPath) : null,
                MetaHash = metaExists ? HashFile(metaPath) : null,
                Guid = metaExists ? ReadGuid(File.ReadAllText(metaPath)) : null,
            };
        }

        private static bool TryNormaliseAssetPath(string rawPath, out string path, out string error)
        {
            path = (rawPath ?? string.Empty).TrimEnd('\0', '\r', '\n').Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
            path = path.TrimEnd('/');

            string[] segments = path.Split('/');
            if (segments.Length < 2 || !string.Equals(segments[0], "Assets", StringComparison.Ordinal) || segments.Any(segment => string.IsNullOrEmpty(segment) || segment == "." || segment == ".."))
            {
                error = "Only normal paths below Assets/ can be imported safely.";
                return false;
            }

            error = null;
            return true;
        }

        private static void AddIssue(BatchPackageReport package, BatchPackageIssueSeverity severity, string assetPath, string message)
        {
            package.Issues.Add(new BatchPackageIssue { Severity = severity, AssetPath = assetPath, Message = message });
        }

        private static string HashFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static string ReadGuid(string metaText)
        {
            using (var reader = new StringReader(metaText ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("guid:", StringComparison.Ordinal)) continue;
                    string guid = trimmed.Substring(5).Trim();
                    return guid.Length == 32 && guid.All(Uri.IsHexDigit) ? guid : null;
                }
            }

            return null;
        }

        private sealed class UnityPackageAsset
        {
            public string PackagePath;
            public string Path;
            public string Guid;
            public bool IsDirectory;
            public string AssetHash;
            public string MetaHash;

            public bool ContentEquals(UnityPackageAsset other)
            {
                return other != null && IsDirectory == other.IsDirectory &&
                       string.Equals(Guid, other.Guid, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(AssetHash, other.AssetHash, StringComparison.Ordinal) &&
                       string.Equals(MetaHash, other.MetaHash, StringComparison.Ordinal);
            }
        }

        private static class UnityPackageArchive
        {
            private const int TarBlockSize = 512;
            private const int SmallEntryLimit = 4 * 1024 * 1024;

            private sealed class RawAsset
            {
                public string Path;
                public string Guid;
                public bool HasAsset;
                public string AssetHash;
                public bool HasMeta;
                public string MetaHash;
            }

            public static IReadOnlyList<UnityPackageAsset> Read(string packagePath)
            {
                if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath)) throw new FileNotFoundException("Package file not found.", packagePath);

                var rawAssets = new Dictionary<string, RawAsset>(StringComparer.Ordinal);
                var header = new byte[TarBlockSize];

                using (FileStream file = File.OpenRead(packagePath))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    while (ReadBlock(gzip, header))
                    {
                        if (IsZeroBlock(header)) break;

                        string entryName = ReadTarName(header);
                        long entrySize = ReadTarSize(header);
                        string archiveId;
                        string leafName;
                        SplitEntryName(entryName, out archiveId, out leafName);

                        if (!rawAssets.TryGetValue(archiveId, out RawAsset raw))
                        {
                            raw = new RawAsset();
                            rawAssets.Add(archiveId, raw);
                        }

                        if (leafName == "pathname")
                        {
                            byte[] bytes = ReadSmallEntry(gzip, entrySize, SmallEntryLimit);
                            raw.Path = Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n');
                        }
                        else if (leafName == "asset")
                        {
                            raw.HasAsset = true;
                            raw.AssetHash = HashEntry(gzip, entrySize);
                        }
                        else if (leafName == "asset.meta")
                        {
                            byte[] bytes = ReadSmallEntry(gzip, entrySize, SmallEntryLimit);
                            raw.HasMeta = true;
                            raw.MetaHash = HashBytes(bytes);
                            raw.Guid = ReadGuid(Encoding.UTF8.GetString(bytes));
                        }
                        else
                        {
                            SkipExact(gzip, entrySize);
                        }

                        long padding = (TarBlockSize - entrySize % TarBlockSize) % TarBlockSize;
                        SkipExact(gzip, padding);
                    }
                }

                var assets = new List<UnityPackageAsset>(rawAssets.Count);
                foreach (RawAsset raw in rawAssets.Values)
                {
                    if (string.IsNullOrEmpty(raw.Path))
                    {
                        if (raw.HasAsset || raw.HasMeta) throw new InvalidDataException("Package entry with asset data has no pathname.");
                        continue;
                    }
                    if (!raw.HasMeta) throw new InvalidDataException($"Package entry '{raw.Path}' has no asset.meta.");

                    assets.Add(new UnityPackageAsset
                    {
                        PackagePath = packagePath,
                        Path = raw.Path,
                        Guid = raw.Guid,
                        IsDirectory = !raw.HasAsset,
                        AssetHash = raw.AssetHash,
                        MetaHash = raw.MetaHash,
                    });
                }

                if (assets.Count == 0) throw new InvalidDataException("No importable Assets entries were found.");
                return assets;
            }

            private static bool ReadBlock(Stream stream, byte[] block)
            {
                int offset = 0;
                while (offset < block.Length)
                {
                    int read = stream.Read(block, offset, block.Length - offset);
                    if (read == 0)
                    {
                        if (offset == 0) return false;
                        throw new EndOfStreamException("Truncated tar header.");
                    }

                    offset += read;
                }

                return true;
            }

            private static bool IsZeroBlock(byte[] block)
            {
                for (int index = 0; index < block.Length; index++)
                {
                    if (block[index] != 0) return false;
                }

                return true;
            }

            private static string ReadTarName(byte[] header)
            {
                string name = ReadAscii(header, 0, 100);
                string prefix = ReadAscii(header, 345, 155);
                return string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
            }

            private static long ReadTarSize(byte[] header)
            {
                long size = 0;
                bool foundDigit = false;
                for (int index = 124; index < 136; index++)
                {
                    byte value = header[index];
                    if (value == 0 || value == (byte)' ') continue;
                    if (value < (byte)'0' || value > (byte)'7') throw new InvalidDataException("Unsupported tar entry size.");
                    foundDigit = true;
                    checked { size = size * 8 + value - (byte)'0'; }
                }

                return foundDigit ? size : 0;
            }

            private static string ReadAscii(byte[] bytes, int offset, int count)
            {
                int length = 0;
                while (length < count && bytes[offset + length] != 0) length++;
                return Encoding.ASCII.GetString(bytes, offset, length);
            }

            private static void SplitEntryName(string entryName, out string archiveId, out string leafName)
            {
                string normalised = (entryName ?? string.Empty).Trim('/');
                int slash = normalised.IndexOf('/');
                if (slash < 0)
                {
                    archiveId = normalised;
                    leafName = string.Empty;
                    return;
                }

                archiveId = normalised.Substring(0, slash);
                leafName = normalised.Substring(slash + 1);
            }

            private static byte[] ReadSmallEntry(Stream stream, long size, int limit)
            {
                if (size < 0 || size > limit) throw new InvalidDataException($"Package metadata entry is too large ({size} bytes).");
                var bytes = new byte[(int)size];
                ReadExact(stream, bytes, 0, bytes.Length);
                return bytes;
            }

            private static string HashEntry(Stream stream, long size)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    var buffer = new byte[64 * 1024];
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int wanted = (int)Math.Min(buffer.Length, remaining);
                        int read = stream.Read(buffer, 0, wanted);
                        if (read == 0) throw new EndOfStreamException("Truncated tar entry.");
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        remaining -= read;
                    }

                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return ToHex(sha.Hash);
                }
            }

            private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    int read = stream.Read(buffer, offset, count);
                    if (read == 0) throw new EndOfStreamException("Truncated tar entry.");
                    offset += read;
                    count -= read;
                }
            }

            private static void SkipExact(Stream stream, long count)
            {
                var buffer = new byte[8192];
                while (count > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, count);
                    int read = stream.Read(buffer, 0, wanted);
                    if (read == 0) throw new EndOfStreamException("Truncated tar entry.");
                    count -= read;
                }
            }
        }
    }
}
