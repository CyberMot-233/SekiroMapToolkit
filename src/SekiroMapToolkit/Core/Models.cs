using System.Collections.Generic;

namespace SekiroMapToolkit.Core
{
    // ---------------- json models (scene.json schema) ----------------
    class MatDef
    {
        public int index { get; set; }
        public string name { get; set; }
        public string mtd { get; set; }
        public string albedo { get; set; }
        public string normal { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string albedoPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string normalPath { get; set; }
    }

    class ObjDef
    {
        public string name { get; set; }
        public int mesh { get; set; }
        public float[] loc { get; set; }
        public float[] quat { get; set; }
        public float[] scale { get; set; }
    }

    class StatsDef
    {
        public int parts { get; set; }
        public int models { get; set; }
        public int objectsPlaced { get; set; }
        public int vertices { get; set; }
        public int textures { get; set; }
        public int seconds { get; set; }
        public int fogSkipped { get; set; }
    }

    class SceneDef
    {
        public string map { get; set; }
        public int meshCount { get; set; }
        public List<MatDef> materials { get; set; }
        public List<ObjDef> objects { get; set; }
        public List<string> missingModels { get; set; }
        public List<string> missingTextures { get; set; }
        public StatsDef stats { get; set; }
    }
}