using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace InfinityModgenManager.Services
{
    public static class SteamService
    {
        public static string? SteamLocation { get; private set; }

        public static bool IsInstalled()
        {
            return !string.IsNullOrWhiteSpace(
                FindSteamLocation());
        }

        public static string? FindSteamLocation()
        {
            if (!string.IsNullOrWhiteSpace(
                    SteamLocation) &&
                Directory.Exists(
                    SteamLocation))
            {
                return SteamLocation;
            }

            string[] registryPaths =
            {
                @"SOFTWARE\Wow6432Node\Valve\Steam",
                @"SOFTWARE\Valve\Steam"
            };

            foreach (string registryPath in registryPaths)
            {
                try
                {
                    using RegistryKey? key =
                        RegistryKey.OpenBaseKey(
                            RegistryHive.LocalMachine,
                            RegistryView.Default)
                        .OpenSubKey(
                            registryPath);

                    if (key?.GetValue(
                            "InstallPath") is string steamPath &&
                        Directory.Exists(
                            steamPath))
                    {
                        SteamLocation =
                            steamPath;

                        return SteamLocation;
                    }
                }
                catch
                {
                    // Try the next detection method.
                }
            }

            try
            {
                using RegistryKey? key =
                    RegistryKey.OpenBaseKey(
                        RegistryHive.CurrentUser,
                        RegistryView.Default)
                    .OpenSubKey(
                        @"Software\Valve\Steam");

                if (key?.GetValue(
                        "SteamPath") is string steamPath &&
                    Directory.Exists(
                        steamPath))
                {
                    SteamLocation =
                        steamPath;

                    return SteamLocation;
                }
            }
            catch
            {
                // Continue with common locations.
            }

            string[] commonLocations =
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam"
            };

            foreach (string path in commonLocations)
            {
                if (Directory.Exists(
                    path))
                {
                    SteamLocation =
                        path;

                    return SteamLocation;
                }
            }

            return null;
        }

        public static bool IsRunning()
        {
            return Process.GetProcessesByName(
                "steam")
                .Any();
        }

        public static bool StartSteam()
        {
            string? steamPath =
                FindSteamLocation();

            if (string.IsNullOrWhiteSpace(
                steamPath))
            {
                return false;
            }

            if (IsRunning())
            {
                return true;
            }

            string steamExecutable =
                Path.Combine(
                    steamPath,
                    "steam.exe");

            if (!File.Exists(
                steamExecutable))
            {
                return false;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            steamExecutable,

                        UseShellExecute =
                            true
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryOpenSteam()
        {
            if (!StartSteam())
            {
                return false;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "steam://open/main",

                        UseShellExecute =
                            true
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryOpenSteamGame(
            ulong appId)
        {
            if (appId == 0)
            {
                return false;
            }

            if (!StartSteam())
            {
                return false;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "steam://run/" +
                            appId,

                        UseShellExecute =
                            true
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<SteamShortcut> GetNonSteamShortcuts()
        {
            var shortcuts =
                new List<SteamShortcut>();

            string? steamPath =
                FindSteamLocation();

            if (string.IsNullOrWhiteSpace(
                steamPath))
            {
                return shortcuts;
            }

            string userdataPath =
                Path.Combine(
                    steamPath,
                    "userdata");

            if (!Directory.Exists(
                userdataPath))
            {
                return shortcuts;
            }

            try
            {
                foreach (string userDirectory in
                         Directory.GetDirectories(
                             userdataPath))
                {
                    string configDirectory =
                        Path.Combine(
                            userDirectory,
                            "config");

                    string shortcutsFile =
                        Path.Combine(
                            configDirectory,
                            "shortcuts.vdf");

                    if (!File.Exists(
                        shortcutsFile))
                    {
                        continue;
                    }

                    try
                    {
                        shortcuts.AddRange(
                            ReadShortcutsVdf(
                                shortcutsFile));
                    }
                    catch
                    {
                        // Ignore invalid shortcut files.
                    }
                }
            }
            catch
            {
                // Ignore inaccessible userdata directories.
            }

            return shortcuts;
        }

        /// <summary>
        /// Finds the non-Steam shortcut that launches the given
        /// executable, regardless of which game or preset it is.
        /// This replaces name-based matching (e.g. "Sonic Omens")
        /// so any game the user has added to Steam works the same way.
        /// </summary>
        public static SteamShortcut? FindShortcutForExecutable(
            string exePath)
        {
            if (string.IsNullOrWhiteSpace(
                exePath))
            {
                return null;
            }

            string normalizedTarget;

            try
            {
                normalizedTarget =
                    Path.GetFullPath(
                        exePath);
            }
            catch
            {
                return null;
            }

            foreach (SteamShortcut shortcut in
                     GetNonSteamShortcuts())
            {
                if (string.IsNullOrWhiteSpace(
                    shortcut.Exe))
                {
                    continue;
                }

                string candidateExe =
                    shortcut.Exe.Trim('"');

                try
                {
                    if (string.Equals(
                            Path.GetFullPath(
                                candidateExe),
                            normalizedTarget,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return shortcut;
                    }
                }
                catch
                {
                    // Malformed path in shortcuts.vdf; skip it.
                }
            }

            return null;
        }

        /// <summary>
        /// Launches a non-Steam shortcut by its Steam AppId.
        /// Use this together with FindShortcutForExecutable, or
        /// with an AppId cached on the Game model, to avoid
        /// re-parsing shortcuts.vdf on every launch.
        /// </summary>
        public static bool TryLaunchByAppId(
            ulong appId)
        {
            if (appId == 0)
            {
                return false;
            }

            if (!StartSteam())
            {
                return false;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "steam://rungameid/" +
                            appId,

                        UseShellExecute =
                            true
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static SteamShortcut? FindSonicOmensShortcut()
        {
            List<SteamShortcut> shortcuts =
                GetNonSteamShortcuts();

            foreach (SteamShortcut shortcut in shortcuts)
            {
                if (IsSonicOmensShortcut(
                    shortcut))
                {
                    return shortcut;
                }
            }

            return null;
        }

        public static bool TryOpenSonicOmensShortcut()
        {
            SteamShortcut? shortcut =
                FindSonicOmensShortcut();

            if (shortcut == null ||
                shortcut.AppId == 0)
            {
                return false;
            }

            if (!StartSteam())
            {
                return false;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "steam://rungameid/" +
                            shortcut.AppId,

                        UseShellExecute =
                            true
                    });

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSonicOmensShortcut(
            SteamShortcut shortcut)
        {
            string appName =
                shortcut.AppName ??
                string.Empty;

            string executable =
                shortcut.Exe ??
                string.Empty;

            string startDirectory =
                shortcut.StartDir ??
                string.Empty;

            return
                appName.Contains(
                    "Sonic Omens",
                    StringComparison.OrdinalIgnoreCase)
                ||
                executable.Contains(
                    "Sonic Omens.exe",
                    StringComparison.OrdinalIgnoreCase)
                ||
                startDirectory.Contains(
                    "Sonic Omens",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static List<SteamShortcut> ReadShortcutsVdf(
            string filePath)
        {
            var shortcuts =
                new List<SteamShortcut>();

            byte[] data =
                File.ReadAllBytes(
                    filePath);

            if (data.Length == 0)
            {
                return shortcuts;
            }

            int position = 0;

            // shortcuts.vdf starts with:
            // 0x00 + "shortcuts" + 0x00
            if (data[position++] != 0x00)
            {
                return shortcuts;
            }

            string rootName =
                ReadNullTerminatedString(
                    data,
                    ref position);

            if (!string.Equals(
                    rootName,
                    "shortcuts",
                    StringComparison.OrdinalIgnoreCase))
            {
                return shortcuts;
            }

            while (position < data.Length)
            {
                byte type =
                    data[position++];

                // End of the shortcuts object.
                if (type == 0x08)
                {
                    break;
                }

                // Each shortcut is a nested object.
                if (type != 0x00)
                {
                    break;
                }

                // Shortcut index: "0", "1", "2", etc.
                ReadNullTerminatedString(
                    data,
                    ref position);

                SteamShortcut? shortcut =
                    ReadShortcutObject(
                        data,
                        ref position);

                if (shortcut != null)
                {
                    shortcuts.Add(
                        shortcut);
                }
            }

            return shortcuts;
        }

        private static SteamShortcut? ReadShortcutObject(
            byte[] data,
            ref int position)
        {
            var shortcut =
                new SteamShortcut();

            while (position < data.Length)
            {
                byte type =
                    data[position++];

                // End of this shortcut object.
                if (type == 0x08)
                {
                    return shortcut;
                }

                string key =
                    ReadNullTerminatedString(
                        data,
                        ref position);

                if (type == 0x01)
                {
                    string value =
                        ReadNullTerminatedString(
                            data,
                            ref position);

                    switch (
                        key.ToLowerInvariant())
                    {
                        case "appname":
                            shortcut.AppName =
                                value;
                            break;

                        case "exe":
                            shortcut.Exe =
                                value;
                            break;

                        case "startdir":
                            shortcut.StartDir =
                                value;
                            break;

                        case "icon":
                            shortcut.Icon =
                                value;
                            break;

                        case "shortcutpath":
                            shortcut.ShortcutPath =
                                value;
                            break;

                        case "launchoptions":
                            shortcut.LaunchOptions =
                                value;
                            break;

                        case "devkitgameid":
                            shortcut.DevkitGameID =
                                value;
                            break;

                        case "flatpakappid":
                            shortcut.FlatpakAppID =
                                value;
                            break;
                    }
                }
                else if (type == 0x02)
                {
                    // Steam stores shortcut appid as a
                    // 32-bit little-endian integer.
                    uint value =
                        ReadUInt32(
                            data,
                            ref position);

                    if (string.Equals(
                            key,
                            "appid",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        /*
                         * Steam's non-Steam shortcut URI uses
                         * the stored 32-bit ID in the upper
                         * 32 bits, with 0x02000000 identifying
                         * the shortcut as a non-Steam game.
                         *
                         * Example:
                         *
                         * Stored ID:
                         * 0xEB05CDEA
                         *
                         * Steam URI ID:
                         * 0xEB05CDEA02000000
                         *
                         * = 16935168378736214016
                         */
                        shortcut.AppId =
                            ((ulong)value << 32) |
                            0x02000000UL;
                    }
                    else if (string.Equals(
                                 key,
                                 "ishidden",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.IsHidden =
                            value;
                    }
                    else if (string.Equals(
                                 key,
                                 "allowdesktopconfig",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.AllowDesktopConfig =
                            value;
                    }
                    else if (string.Equals(
                                 key,
                                 "allowoverlay",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.AllowOverlay =
                            value;
                    }
                    else if (string.Equals(
                                 key,
                                 "openvr",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.OpenVR =
                            value;
                    }
                    else if (string.Equals(
                                 key,
                                 "devkit",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.Devkit =
                            value;
                    }
                    else if (string.Equals(
                                 key,
                                 "lastplaytime",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        shortcut.LastPlayTime =
                            value;
                    }
                }
                else if (type == 0x00)
                {
                    // Nested object, such as "tags".
                    SkipVdfObject(
                        data,
                        ref position);
                }
                else
                {
                    return null;
                }
            }

            return shortcut;
        }

        private static void SkipVdfObject(
            byte[] data,
            ref int position)
        {
            int depth = 1;

            while (
                position < data.Length &&
                depth > 0)
            {
                byte type =
                    data[position++];

                if (type == 0x08)
                {
                    depth--;
                    continue;
                }

                if (type == 0x00)
                {
                    ReadNullTerminatedString(
                        data,
                        ref position);

                    depth++;
                    continue;
                }

                ReadNullTerminatedString(
                    data,
                    ref position);

                if (type == 0x01)
                {
                    ReadNullTerminatedString(
                        data,
                        ref position);
                }
                else if (type == 0x02)
                {
                    if (position + 4 >
                        data.Length)
                    {
                        return;
                    }

                    position += 4;
                }
                else
                {
                    return;
                }
            }
        }

        private static uint ReadUInt32(
            byte[] data,
            ref int position)
        {
            if (position + 4 >
                data.Length)
            {
                position =
                    data.Length;

                return 0;
            }

            uint value =
                (uint)data[position]
                |
                ((uint)data[position + 1] << 8)
                |
                ((uint)data[position + 2] << 16)
                |
                ((uint)data[position + 3] << 24);

            position += 4;

            return value;
        }

        private static string ReadNullTerminatedString(
            byte[] data,
            ref int position)
        {
            int start =
                position;

            while (
                position < data.Length &&
                data[position] != 0x00)
            {
                position++;
            }

            string value =
                Encoding.UTF8.GetString(
                    data,
                    start,
                    position - start);

            if (position < data.Length)
            {
                position++;
            }

            return value;
        }
    }

    public sealed class SteamShortcut
    {
        public ulong AppId { get; set; }

        public string AppName { get; set; } =
            string.Empty;

        public string Exe { get; set; } =
            string.Empty;

        public string StartDir { get; set; } =
            string.Empty;

        public string Icon { get; set; } =
            string.Empty;

        public string ShortcutPath { get; set; } =
            string.Empty;

        public string LaunchOptions { get; set; } =
            string.Empty;

        public string DevkitGameID { get; set; } =
            string.Empty;

        public string FlatpakAppID { get; set; } =
            string.Empty;

        public uint IsHidden { get; set; }

        public uint AllowDesktopConfig { get; set; }

        public uint AllowOverlay { get; set; }

        public uint OpenVR { get; set; }

        public uint Devkit { get; set; }

        public uint LastPlayTime { get; set; }
    }
}