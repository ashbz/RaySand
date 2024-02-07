global using System.Numerics;
global using Raylib_CsLo;

global using static Raylib_CsLo.Raylib;
global using static Raylib_CsLo.RayMath;
global using static Raylib_CsLo.RayGui;
global using static Raylib_CsLo.RlGl;

global using static Raylib_CsLo.KeyboardKey;
global using static Raylib_CsLo.MouseButton;
global using static Raylib_CsLo.ConfigFlags;
global using RenderTexture2D = Raylib_CsLo.RenderTexture;

internal class Program
{
    private static void Main(string[] args)
    {
        RaySand.RaySand.main();
    }
}