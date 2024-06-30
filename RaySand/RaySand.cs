using Microsoft.Toolkit.HighPerformance;
using Newtonsoft.Json;
using Raylib_CsLo;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static RaySand.Helper;

namespace RaySand
{
    public static class RaySand
    {


        static SandWorld sw;



        public static int main()
        {
            const int screenWidth = 1280;
            const int screenHeight = 720;

            SetConfigFlags(FLAG_WINDOW_RESIZABLE | FLAG_WINDOW_HIGHDPI | FLAG_VSYNC_HINT);
            InitWindow(screenWidth, screenHeight, "Sand");

            sw = new SandWorld();

            while (!WindowShouldClose())
            {
                //BeginDrawing();
                sw.UpdateAndDrawSandWorld();
            }

            CloseWindow();

            return 0;
        }

        
    }

}