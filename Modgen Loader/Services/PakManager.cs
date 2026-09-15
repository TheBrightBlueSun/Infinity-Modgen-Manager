using Modgen_Loader.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Modgen_Loader.Services
{
    public class PakManager
    {
        private const string ManagedModsFolderName = "~mods";

        private const string ManagedMoviesFolderName =
            ".infinity_modgen_movie_backups";

        private const string ManagedMoviesManifestFileName =
            ".infinity_modgen_movies.json";

        public void ApplyMods(
            Game game,
            IEnumerable<Mod> mods)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));

            List<Mod> enabledMods =
                mods
                    .Where(mod => mod.IsEnabled)
                    .OrderBy(mod => mod.LoadOrder)
                    .ToList();

            ApplyPakMods(
                game,
                enabledMods);

            ApplyMovieMods(
                game,
                enabledMods);
        }

        private void ApplyPakMods(
            Game game,
            IEnumerable<Mod> enabledMods)
        {
            if (!game.SupportsPakMods)
                return;

            if (string.IsNullOrWhiteSpace(
                game.PaksDirectory))
            {
                throw new IOException(
                    "The game's PAK directory has not been configured.");
            }

            string managedModsDirectory =
                Path.Combine(
                    game.PaksDirectory,
                    ManagedModsFolderName);

            Directory.CreateDirectory(
                managedModsDirectory);

            /*
             * Remove every folder previously managed
             * by Infinity Modgen Loader.
             *
             * CrossPatch uses priority-prefixed folders,
             * for example:
             *
             * 000.Project Reicho
             * 001.Another Mod
             */
            CleanManagedModFolders(
                managedModsDirectory);

            List<Mod> pakMods =
                enabledMods
                    .Where(mod =>
                        string.Equals(
                            mod.ModType,
                            "PAK",
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            int priority = 0;

            foreach (Mod mod in pakMods)
            {
                if (string.IsNullOrWhiteSpace(
                    mod.PakPath))
                {
                    continue;
                }

                if (!File.Exists(
                    mod.PakPath))
                {
                    continue;
                }

                string modName =
                    GetSafeModName(
                        mod);

                string priorityFolderName =
                    priority.ToString("D3") +
                    "." +
                    modName;

                string destinationFolder =
                    Path.Combine(
                        managedModsDirectory,
                        priorityFolderName);

                Directory.CreateDirectory(
                    destinationFolder);

                CopyPakFiles(
                    mod,
                    destinationFolder);

                priority++;
            }
        }

        private void ApplyMovieMods(
            Game game,
            IEnumerable<Mod> enabledMods)
        {
            if (string.IsNullOrWhiteSpace(
                game.GameDirectory))
            {
                return;
            }

            string gameContentDirectory =
                !string.IsNullOrWhiteSpace(
                    game.EngineContentDirectory)
                    ? game.EngineContentDirectory
                    : Path.Combine(
                        game.GameDirectory,
                        "Content");

            string gameMoviesDirectory =
                Path.Combine(
                    gameContentDirectory,
                    "Movies");

            Directory.CreateDirectory(
                gameMoviesDirectory);

            string backupDirectory =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesFolderName);

            Directory.CreateDirectory(
                backupDirectory);

            Dictionary<string, ManagedMovieInfo>
                previousManagedMovies =
                    LoadManagedMovieFiles(
                        gameMoviesDirectory);

            /*
             * First restore everything that was installed
             * by the previous Apply operation.
             *
             * This gets the game back to its original state
             * before we calculate the new mod state.
             */
            RestorePreviousMovies(
                gameMoviesDirectory,
                backupDirectory,
                previousManagedMovies);

            Dictionary<string, ManagedMovieInfo>
                currentManagedMovies =
                    new Dictionary<string, ManagedMovieInfo>(
                        StringComparer.OrdinalIgnoreCase);

            foreach (Mod mod in enabledMods)
            {
                string sourceMoviesDirectory =
                    Path.Combine(
                        mod.ModDirectory,
                        "Content",
                        "Movies");

                if (!Directory.Exists(
                    sourceMoviesDirectory))
                {
                    continue;
                }

                string[] movieFiles =
                    Directory.GetFiles(
                        sourceMoviesDirectory,
                        "*",
                        SearchOption.AllDirectories);

                foreach (string sourceMovie in movieFiles)
                {
                    string relativeMoviePath =
                        Path.GetRelativePath(
                            sourceMoviesDirectory,
                            sourceMovie);

                    if (string.IsNullOrWhiteSpace(
                        relativeMoviePath))
                    {
                        continue;
                    }

                    string destinationPath =
                        Path.Combine(
                            gameMoviesDirectory,
                            relativeMoviePath);

                    /*
                     * If this movie has not already been managed,
                     * check whether the original game has a movie
                     * with the same filename.
                     */
                    if (!currentManagedMovies.ContainsKey(
                        relativeMoviePath))
                    {
                        bool hadOriginal =
                            File.Exists(
                                destinationPath);

                        string backupPath =
                            Path.Combine(
                                backupDirectory,
                                relativeMoviePath);

                        if (hadOriginal)
                        {
                            string? backupParent =
                                Path.GetDirectoryName(
                                    backupPath);

                            if (!string.IsNullOrWhiteSpace(
                                backupParent))
                            {
                                Directory.CreateDirectory(
                                    backupParent);
                            }

                            File.Copy(
                                destinationPath,
                                backupPath,
                                true);
                        }

                        currentManagedMovies[
                            relativeMoviePath] =
                            new ManagedMovieInfo
                            {
                                RelativePath =
                                    relativeMoviePath,

                                HadOriginal =
                                    hadOriginal
                            };
                    }

                    string? destinationDirectory =
                        Path.GetDirectoryName(
                            destinationPath);

                    if (!string.IsNullOrWhiteSpace(
                        destinationDirectory))
                    {
                        Directory.CreateDirectory(
                            destinationDirectory);
                    }

                    /*
                     * Mods are processed in LoadOrder.
                     *
                     * Therefore a later mod replaces the movie
                     * supplied by an earlier mod.
                     */
                    File.Copy(
                        sourceMovie,
                        destinationPath,
                        true);
                }
            }

            /*
             * Remove backups belonging to movies that are no
             * longer managed.
             */
            CleanUnusedMovieBackups(
                backupDirectory,
                currentManagedMovies);

            SaveManagedMovieFiles(
                gameMoviesDirectory,
                currentManagedMovies);
        }

        private void RestorePreviousMovies(
            string gameMoviesDirectory,
            string backupDirectory,
            Dictionary<string, ManagedMovieInfo>
                previousManagedMovies)
        {
            foreach (
                KeyValuePair<
                    string,
                    ManagedMovieInfo> entry
                in previousManagedMovies)
            {
                string relativeMoviePath =
                    entry.Key;

                ManagedMovieInfo movieInfo =
                    entry.Value;

                string destinationPath =
                    Path.Combine(
                        gameMoviesDirectory,
                        relativeMoviePath);

                if (movieInfo.HadOriginal)
                {
                    string backupPath =
                        Path.Combine(
                            backupDirectory,
                            relativeMoviePath);

                    if (File.Exists(
                        backupPath))
                    {
                        string? destinationDirectory =
                            Path.GetDirectoryName(
                                destinationPath);

                        if (!string.IsNullOrWhiteSpace(
                            destinationDirectory))
                        {
                            Directory.CreateDirectory(
                                destinationDirectory);
                        }

                        File.Copy(
                            backupPath,
                            destinationPath,
                            true);
                    }
                }
                else
                {
                    /*
                     * The file did not exist before the mod
                     * was installed, so remove the mod's copy.
                     */
                    if (File.Exists(
                        destinationPath))
                    {
                        File.Delete(
                            destinationPath);
                    }
                }
            }
        }

        private Dictionary<string, ManagedMovieInfo>
            LoadManagedMovieFiles(
                string gameMoviesDirectory)
        {
            string manifestPath =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesManifestFileName);

            if (!File.Exists(
                manifestPath))
            {
                return
                    new Dictionary<string, ManagedMovieInfo>(
                        StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                string json =
                    File.ReadAllText(
                        manifestPath);

                List<ManagedMovieInfo>? files =
                    JsonSerializer.Deserialize<
                        List<ManagedMovieInfo>>(
                            json);

                if (files == null)
                {
                    return
                        new Dictionary<
                            string,
                            ManagedMovieInfo>(
                            StringComparer.OrdinalIgnoreCase);
                }

                return
                    files
                        .Where(file =>
                            !string.IsNullOrWhiteSpace(
                                file.RelativePath))
                        .GroupBy(
                            file => file.RelativePath,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Last(),
                            StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                /*
                 * If the manifest cannot be read,
                 * do not risk deleting arbitrary files.
                 */
                return
                    new Dictionary<
                        string,
                        ManagedMovieInfo>(
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveManagedMovieFiles(
            string gameMoviesDirectory,
            Dictionary<string, ManagedMovieInfo>
                managedMovies)
        {
            string manifestPath =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesManifestFileName);

            List<ManagedMovieInfo> files =
                managedMovies
                    .Values
                    .OrderBy(
                        file => file.RelativePath)
                    .ToList();

            string json =
                JsonSerializer.Serialize(
                    files,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                manifestPath,
                json);
        }

        private void CleanUnusedMovieBackups(
            string backupDirectory,
            Dictionary<string, ManagedMovieInfo>
                currentManagedMovies)
        {
            if (!Directory.Exists(
                backupDirectory))
            {
                return;
            }

            string[] backupFiles =
                Directory.GetFiles(
                    backupDirectory,
                    "*",
                    SearchOption.AllDirectories);

            foreach (string backupFile in backupFiles)
            {
                string relativePath =
                    Path.GetRelativePath(
                        backupDirectory,
                        backupFile);

                if (!currentManagedMovies.ContainsKey(
                    relativePath))
                {
                    File.Delete(
                        backupFile);
                }
            }

            DeleteEmptyDirectories(
                backupDirectory);
        }

        private void DeleteEmptyDirectories(
            string directory)
        {
            if (!Directory.Exists(
                directory))
            {
                return;
            }

            foreach (string childDirectory in
                     Directory.GetDirectories(
                         directory))
            {
                DeleteEmptyDirectories(
                    childDirectory);

                if (!Directory.EnumerateFileSystemEntries(
                    childDirectory).Any())
                {
                    Directory.Delete(
                        childDirectory);
                }
            }
        }

        private void CleanManagedModFolders(
            string managedModsDirectory)
        {
            if (!Directory.Exists(
                managedModsDirectory))
            {
                return;
            }

            string[] directories =
                Directory.GetDirectories(
                    managedModsDirectory);

            foreach (string directory in directories)
            {
                string directoryName =
                    Path.GetFileName(
                        directory);

                if (string.IsNullOrWhiteSpace(
                    directoryName))
                {
                    continue;
                }

                if (IsManagedPriorityFolder(
                    directoryName))
                {
                    Directory.Delete(
                        directory,
                        true);
                }
            }
        }

        private bool IsManagedPriorityFolder(
            string directoryName)
        {
            if (directoryName.Length < 5)
                return false;

            int dotIndex =
                directoryName.IndexOf('.');

            if (dotIndex < 3)
                return false;

            string priority =
                directoryName.Substring(
                    0,
                    dotIndex);

            if (!int.TryParse(
                priority,
                out _))
            {
                return false;
            }

            return true;
        }

        private void CopyPakFiles(
            Mod mod,
            string destinationFolder)
        {
            string sourceDirectory =
                mod.ModDirectory;

            if (!Directory.Exists(
                sourceDirectory))
            {
                return;
            }

            /*
             * A mod can contain more than one PAK,
             * so copy every PAK/UCAS/UTOC belonging
             * to the mod.
             */
            string[] files =
                Directory.GetFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly);

            foreach (string sourceFile in files)
            {
                string extension =
                    Path.GetExtension(
                        sourceFile);

                if (!IsPakRelatedFile(
                    extension))
                {
                    continue;
                }

                string sourceFileName =
                    Path.GetFileName(
                        sourceFile);

                if (string.IsNullOrWhiteSpace(
                    sourceFileName))
                {
                    continue;
                }

                string destinationFileName =
                    AddPSuffix(
                        sourceFileName);

                string destinationPath =
                    Path.Combine(
                        destinationFolder,
                        destinationFileName);

                File.Copy(
                    sourceFile,
                    destinationPath,
                    true);
            }

            /*
             * If the mod.ini explicitly identifies a
             * PAK somewhere outside the mod directory,
             * make sure that PAK is also copied.
             */
            if (!string.IsNullOrWhiteSpace(
                mod.PakPath) &&
                File.Exists(
                    mod.PakPath))
            {
                string sourceFileName =
                    Path.GetFileName(
                        mod.PakPath);

                if (!string.IsNullOrWhiteSpace(
                    sourceFileName))
                {
                    string destinationFileName =
                        AddPSuffix(
                            sourceFileName);

                    string destinationPath =
                        Path.Combine(
                            destinationFolder,
                            destinationFileName);

                    File.Copy(
                        mod.PakPath,
                        destinationPath,
                        true);
                }
            }
        }

        private bool IsPakRelatedFile(
            string extension)
        {
            return
                string.Equals(
                    extension,
                    ".pak",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    extension,
                    ".ucas",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    extension,
                    ".utoc",
                    StringComparison.OrdinalIgnoreCase);
        }

        private string AddPSuffix(
            string fileName)
        {
            string extension =
                Path.GetExtension(
                    fileName);

            string nameWithoutExtension =
                Path.GetFileNameWithoutExtension(
                    fileName);

            if (nameWithoutExtension
                .EndsWith(
                    "_P",
                    StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return
                nameWithoutExtension +
                "_P" +
                extension;
        }

        private string GetSafeModName(
            Mod mod)
        {
            string name =
                mod.Name;

            if (string.IsNullOrWhiteSpace(
                name))
            {
                name =
                    Path.GetFileName(
                        mod.ModDirectory);
            }

            if (string.IsNullOrWhiteSpace(
                name))
            {
                name = "Unnamed Mod";
            }

            foreach (char invalidCharacter in
                     Path.GetInvalidFileNameChars())
            {
                name =
                    name.Replace(
                        invalidCharacter,
                        '_');
            }

            return name.Trim();
        }

        private class ManagedMovieInfo
        {
            public string RelativePath { get; set; } = "";

            public bool HadOriginal { get; set; }
        }
    }
}