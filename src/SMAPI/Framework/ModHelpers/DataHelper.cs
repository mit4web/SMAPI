using System;
using System.IO;
using Newtonsoft.Json;
using StardewModdingAPI.Toolkit.Serialisation;
using StardewModdingAPI.Toolkit.Utilities;
using StardewValley;

namespace StardewModdingAPI.Framework.ModHelpers
{
    /// <summary>Provides an API for reading and storing local mod data.</summary>
    internal class DataHelper : BaseHelper, IDataHelper
    {
        /*********
        ** Properties
        *********/
        /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
        private readonly JsonHelper JsonHelper;

        /// <summary>The absolute path to the mod folder.</summary>
        private readonly string ModFolderPath;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="modID">The unique ID of the relevant mod.</param>
        /// <param name="modFolderPath">The absolute path to the mod folder.</param>
        /// <param name="jsonHelper">The absolute path to the mod folder.</param>
        public DataHelper(string modID, string modFolderPath, JsonHelper jsonHelper)
            : base(modID)
        {
            this.ModFolderPath = modFolderPath;
            this.JsonHelper = jsonHelper;
        }

        /****
        ** JSON file
        ****/
        /// <summary>Read data from a JSON file in the mod's folder.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="path">The file path relative to the mod folder.</param>
        /// <returns>Returns the deserialised model, or <c>null</c> if the file doesn't exist or is empty.</returns>
        /// <exception cref="InvalidOperationException">The <paramref name="path"/> is not relative or contains directory climbing (../).</exception>
        public TModel ReadJsonFile<TModel>(string path) where TModel : class
        {
            if (!PathUtilities.IsSafeRelativePath(path))
                throw new InvalidOperationException($"You must call {nameof(IModHelper.Data)}.{nameof(this.ReadJsonFile)} with a relative path.");

            path = Path.Combine(this.ModFolderPath, PathUtilities.NormalisePathSeparators(path));
            return this.JsonHelper.ReadJsonFileIfExists(path, out TModel data)
                ? data
                : null;
        }

        /// <summary>Save data to a JSON file in the mod's folder.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="path">The file path relative to the mod folder.</param>
        /// <param name="data">The arbitrary data to save.</param>
        /// <exception cref="InvalidOperationException">The <paramref name="path"/> is not relative or contains directory climbing (../).</exception>
        public void WriteJsonFile<TModel>(string path, TModel data) where TModel : class
        {
            if (!PathUtilities.IsSafeRelativePath(path))
                throw new InvalidOperationException($"You must call {nameof(IMod.Helper)}.{nameof(IModHelper.Data)}.{nameof(this.WriteJsonFile)} with a relative path (without directory climbing).");

            path = Path.Combine(this.ModFolderPath, PathUtilities.NormalisePathSeparators(path));
            this.JsonHelper.WriteJsonFile(path, data);
        }

        /****
        ** Save file
        ****/
        /// <summary>Read arbitrary data stored in the current save slot. This is only possible if a save has been loaded.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="key">The unique key identifying the data.</param>
        /// <returns>Returns the parsed data, or <c>null</c> if the entry doesn't exist or is empty.</returns>
        /// <exception cref="InvalidOperationException">The player hasn't loaded a save file yet or isn't the main player.</exception>
        public TModel ReadSaveData<TModel>(string key) where TModel : class
        {
            if (!Context.IsSaveLoaded)
                throw new InvalidOperationException($"Can't use {nameof(IMod.Helper)}.{nameof(IModHelper.Data)}.{nameof(this.ReadSaveData)} when a save file isn't loaded.");
            if (!Context.IsMainPlayer)
                throw new InvalidOperationException($"Can't use {nameof(IMod.Helper)}.{nameof(IModHelper.Data)}.{nameof(this.ReadSaveData)} because this isn't the main player. (Save files are stored on the main player's computer.)");

            return Game1.CustomData.TryGetValue(this.GetSaveFileKey(key), out string value)
                ? this.JsonHelper.Deserialise<TModel>(value)
                : null;
        }

        /// <summary>Save arbitrary data to the current save slot. This is only possible if a save has been loaded, and the data will be lost if the player exits without saving the current day.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="key">The unique key identifying the data.</param>
        /// <param name="data">The arbitrary data to save.</param>
        /// <exception cref="InvalidOperationException">The player hasn't loaded a save file yet or isn't the main player.</exception>
        public void WriteSaveData<TModel>(string key, TModel data) where TModel : class
        {
            if (!Context.IsSaveLoaded)
                throw new InvalidOperationException($"Can't use {nameof(IMod.Helper)}.{nameof(IModHelper.Data)}.{nameof(this.WriteSaveData)} when a save file isn't loaded.");
            if (!Context.IsMainPlayer)
                throw new InvalidOperationException($"Can't use {nameof(IMod.Helper)}.{nameof(IModHelper.Data)}.{nameof(this.ReadSaveData)} because this isn't the main player. (Save files are stored on the main player's computer.)");

            Game1.CustomData[this.GetSaveFileKey(key)] = this.JsonHelper.Serialise(data, Formatting.None);
        }

        /****
        ** Global app data
        ****/
        /// <summary>Read arbitrary data stored on the local computer, synchronised by GOG/Steam if applicable.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="key">The unique key identifying the data.</param>
        /// <returns>Returns the parsed data, or <c>null</c> if the entry doesn't exist or is empty.</returns>
        public TModel ReadGlobalData<TModel>(string key) where TModel : class
        {
            string path = this.GetGlobalDataPath(key);
            return this.JsonHelper.ReadJsonFileIfExists(path, out TModel data)
                ? data
                : null;
        }

        /// <summary>Save arbitrary data to the local computer, synchronised by GOG/Steam if applicable.</summary>
        /// <typeparam name="TModel">The model type. This should be a plain class that has public properties for the data you want. The properties can be complex types.</typeparam>
        /// <param name="key">The unique key identifying the data.</param>
        /// <param name="data">The arbitrary data to save.</param>
        public void WriteGlobalData<TModel>(string key, TModel data) where TModel : class
        {
            string path = this.GetGlobalDataPath(key);
            this.JsonHelper.WriteJsonFile(path, data);
        }


        /*********
        ** Public methods
        *********/
        /// <summary>Get the unique key for a save file data entry.</summary>
        /// <param name="key">The unique key identifying the data.</param>
        private string GetSaveFileKey(string key)
        {
            this.AssertSlug(key, nameof(key));
            return $"smapi/mod-data/{this.ModID}/{key}".ToLower();
        }

        /// <summary>Get the absolute path for a global data file.</summary>
        /// <param name="key">The unique key identifying the data.</param>
        private string GetGlobalDataPath(string key)
        {
            this.AssertSlug(key, nameof(key));
            return Path.Combine(Constants.SavesPath, ".smapi", "mod-data", this.ModID.ToLower(), $"{key}.json".ToLower());
        }

        /// <summary>Assert that a key contains only characters that are safe in all contexts.</summary>
        /// <param name="key">The key to check.</param>
        /// <param name="paramName">The argument name for any assertion error.</param>
        private void AssertSlug(string key, string paramName)
        {
            if (!PathUtilities.IsSlug(key))
                throw new ArgumentException("The data key is invalid (keys must only contain letters, numbers, underscores, periods, or hyphens).", paramName);
        }
    }
}
