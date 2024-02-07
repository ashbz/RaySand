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
            NorthWest = 7,
            North = 8,
            NorthEast = 9,
            East = 6,
            West = 4,
            SouthEast = 3,
            South = 2,
            SouthWest = 1,
            AllDirections = 255
        }
    }
}
