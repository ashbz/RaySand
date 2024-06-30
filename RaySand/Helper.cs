using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaySand
{
    public static class Helper
    {
        [Flags]
        public enum Directions
        {
            SouthWest = 1,
            South = 2,
            SouthEast = 3,
            West = 4,
            East = 6,
            NorthWest = 7,
            North = 8,
            NorthEast = 9,
            AllDirections = 255
        }

        public static void Shuffle<T>(this Random rng, T[] array)
        {
            int n = array.Length;
            while (n > 1)
            {
                int k = rng.Next(n--);
                T temp = array[n];
                array[n] = array[k];
                array[k] = temp;
            }
        }


        public static MyColor ChangeColorBrightness(MyColor color, float correctionFactor)
        {
            float red = (float)color.R;
            float green = (float)color.G;
            float blue = (float)color.B;

            if (correctionFactor < 0)
            {
                correctionFactor = 1 + correctionFactor;
                red *= correctionFactor;
                green *= correctionFactor;
                blue *= correctionFactor;
            }
            else
            {
                red = (255 - red) * correctionFactor + red;
                green = (255 - green) * correctionFactor + green;
                blue = (255 - blue) * correctionFactor + blue;
            }

            return new MyColor((int)red, (int)green, (int)blue);
        }
    }
}
