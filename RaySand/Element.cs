using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaySand
{
    public class Element
    {
        public Element()
        {
            behavior = new List<int[]>();
        }
        public int id { get; set; }
        public string name { get; set; }
        public float[] minColor { get; set; }
        public float[] maxColor { get; set; }
        public int minSpeed { get; set; }
        public string generatesMaterial { get; set; }
        public int[] generatedMaterialIds { get; set; }
        public int generatorFrequency { get; set; }
        public int maxSpeed { get; set; }
        public int density { get; set; }
        public int deathChance { get; set; }
        public bool solid { get; set; }
        public bool flaming { get; set; }
        public bool flammable { get; set; }
        public bool melting { get; set; }
        public bool meltable { get; set; }
        public List<int[]> behavior { get; set; }

        public int GetRandomSpeed()
        {
            return GetRandomValue(minSpeed, maxSpeed);
        }

        public bool IsFrozen()
        {
            return minSpeed == 0 && maxSpeed == 0;
        }

        public bool IsGenerator()
        {
            return generatorFrequency > 0 && !string.IsNullOrEmpty(generatesMaterial);
        }
    }
}
