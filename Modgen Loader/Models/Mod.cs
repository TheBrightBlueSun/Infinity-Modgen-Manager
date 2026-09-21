namespace InfinityModgenManager.Models
{
    public class Mod
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";

        public string PakPath { get; set; } = "";
        public string PakFileName { get; set; } = "";

        public string ModDirectory { get; set; } = "";

        public string ModType { get; set; } = "PAK";

        public bool IsEnabled { get; set; } = false;

        public int LoadOrder { get; set; } = 0;
    }
}