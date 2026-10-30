using System;
using System.Collections.Generic;
using System.IO;

namespace OddJobs.IO
{
    public enum FileKind { Other, Photo, Video, Audio, Document, Archive, Installer, Code, Font, DiskImage, System }

    /// <summary>Groups file extensions into the kinds of things people actually think in.</summary>
    public static class FileKinds
    {
        static readonly Dictionary<string, FileKind> Map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);

        static FileKinds()
        {
            Add(FileKind.Photo, "jpg jpeg jpe jfif png gif bmp tif tiff heic heif webp avif svg ico raw cr2 cr3 nef arw dng orf rw2 psd ai xcf");
            Add(FileKind.Video, "mp4 m4v mov avi mkv wmv webm flv 3gp mts m2ts mpg mpeg vob");
            Add(FileKind.Audio, "mp3 m4a aac flac wav ogg oga opus wma aiff aif mid midi m4b");
            Add(FileKind.Document, "pdf doc docx docm odt rtf txt md xls xlsx xlsm ods csv tsv ppt pptx odp pages numbers key epub mobi azw3 one vsdx xps oxps");
            Add(FileKind.Archive, "zip rar 7z tar gz tgz bz2 xz zst cab lz lzma");
            Add(FileKind.Installer, "exe msi msix msixbundle appx appxbundle bat cmd ps1 apk");
            Add(FileKind.Code, "cs js ts jsx tsx py java c cpp h hpp go rs rb php html htm css scss json xml yaml yml toml ini cfg sql sh ipynb");
            Add(FileKind.Font, "ttf otf woff woff2");
            Add(FileKind.DiskImage, "iso img vhd vhdx vmdk dmg");
            Add(FileKind.System, "dll sys tmp log dat bak cache db lock pdb");
        }

        static void Add(FileKind kind, string extensions)
        {
            foreach (var e in extensions.Split(' ')) Map["." + e] = kind;
        }

        public static FileKind Of(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Length > 0 && Map.TryGetValue(ext, out var kind) ? kind : FileKind.Other;
        }

        public static string Label(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Photo: return "Images";
                case FileKind.Video: return "Videos";
                case FileKind.Audio: return "Music & audio";
                case FileKind.Document: return "Documents";
                case FileKind.Archive: return "Archives";
                case FileKind.Installer: return "Installers & apps";
                case FileKind.Code: return "Code";
                case FileKind.Font: return "Fonts";
                case FileKind.DiskImage: return "Disk images";
                case FileKind.System: return "System & temporary";
                default: return "Other";
            }
        }

        /// <summary>Extensions that are partial downloads still being written by a browser.</summary>
        public static bool IsPartialDownload(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".crdownload":
                case ".part":
                case ".partial":
                case ".download":
                case ".opdownload":
                case ".!ut":
                case ".tmp":
                    return true;
                default:
                    return false;
            }
        }
    }
}
