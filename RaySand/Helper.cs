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
    }
}
