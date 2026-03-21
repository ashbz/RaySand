using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static RaySand.Helper;

namespace RaySand
{
    public class SandWorld
    {
        public bool _PAUSE_FLAG = false;
        public bool _PARALLEL_FLAG = true;
        public bool _FAST_DRAW_FLAG = false;
        public bool _DEBUG_FLAG = false;

        public Element current_element;

        const string _ELEMENT_FILEPATH = @"elements.json";
        const int _GRID_PIXEL_SIZE = 32;
        public const int _WORLD_WIDTH = 400;
        public const int _WORLD_HEIGHT = 240;
        const int _DIRTY_CHUNK_SIZE = 10;
        public int _DIRTY_WORLD_WIDTH;
        public int _DIRTY_WORLD_HEIGHT;
        const int _UI_WIDTH = 140;
        const int _UI_HEIGHT = 480;
        public int CURSOR_SIZE = 40;
        public int speed_multiplier = 3;
        public Camera2D world_camera;
        public FileSystemWatcher file_watcher;
        public DateTime element_file_last_read_dt = DateTime.MinValue;

        public ConcurrentDictionary<int, Element> ALL_ELEMENTS;
        public readonly Element EMPTY_ELEMENT = new() { id = 0, name = "None" };

        public Element[,] new_world = new Element[_WORLD_WIDTH, _WORLD_HEIGHT];
        public bool[,] dirty_world_chunks;
        public bool[,] old_dirty_world_chunks;
        public int[,] world_color_map = new int[_WORLD_WIDTH, _WORLD_HEIGHT];

        public ConcurrentDictionary<int, Color> COLOR_CACHE;
        public Dictionary<int, Tuple<Rectangle, Material>> GUI_MATERIALS = new();
        public RenderTexture2D target;

        private bool wasMouseDown;

        // Thread-local RNG — avoids Raylib GetRandomValue overhead and contention in parallel loops
        [ThreadStatic] private static Random _rng;
        private static int Rnd(int min, int max) => (_rng ??= new Random()).Next(min, max + 1);

        // Per-sub-step cell claiming — prevents double-processing & destination collisions in parallel simulation.
        // Cleared to 0 before every sub-step; claiming sets the value to 1.
        private readonly int[,] _cellClaimed = new int[_WORLD_WIDTH, _WORLD_HEIGHT];

        // Element name → Element cache for O(1) lookup (replaces LINQ in hot TryMoveElement path)
        private volatile Dictionary<string, Element> _elementsByName = new();

        // Valid direction values (enum skips 5 — numpad centre — so we wrap within this set)
        private static readonly int[] _validDirs = { 1, 2, 3, 4, 6, 7, 8, 9 };

        public SandWorld()
        {
            world_camera.target = new(_WORLD_WIDTH / 2.0f, _WORLD_HEIGHT / 2.0f);
            world_camera.offset = new(_WORLD_WIDTH / 2.0f, _WORLD_HEIGHT / 2.0f - 60);
            world_camera.rotation = 0.0f;
            world_camera.zoom = 0.08f;

            new_world = new Element[_WORLD_WIDTH, _WORLD_HEIGHT];

            _DIRTY_WORLD_WIDTH = _WORLD_WIDTH / _DIRTY_CHUNK_SIZE;
            _DIRTY_WORLD_HEIGHT = _WORLD_HEIGHT / _DIRTY_CHUNK_SIZE;

            dirty_world_chunks = new bool[_DIRTY_WORLD_WIDTH, _DIRTY_WORLD_HEIGHT];
            old_dirty_world_chunks = new bool[_DIRTY_WORLD_WIDTH, _DIRTY_WORLD_HEIGHT];

            world_color_map = new int[_WORLD_WIDTH, _WORLD_HEIGHT];

            for (int x = 0; x < _WORLD_WIDTH; x++)
                for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                {
                    world_color_map[x, y] = GetRandomValue(1, 3);
                    new_world[x, y] = EMPTY_ELEMENT;
                }

            LoadElements();
            SetupFilewatch();
        }

        private Element GetElementById(int id)
        {
            if (ALL_ELEMENTS == null) ALL_ELEMENTS = new ConcurrentDictionary<int, Element>();
            if (id == 0) return EMPTY_ELEMENT;
            return ALL_ELEMENTS[id];
        }

        // O(1) element lookup by name — use this instead of LINQ in hot paths
        private Element GetElementByName(string name)
            => _elementsByName.TryGetValue(name, out var e) ? e : EMPTY_ELEMENT;

        public async void LoadElements()
        {
            var newPath = Path.GetTempPath() + Path.GetFileName(_ELEMENT_FILEPATH);
            File.Copy(_ELEMENT_FILEPATH, newPath, true);
            string jsonData;
            using (var fs = new FileStream(newPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var sr = new StreamReader(fs))
                jsonData = sr.ReadToEnd();

            if (string.IsNullOrEmpty(jsonData))
            {
                await Task.Delay(200);
                LoadElements();
                return;
            }

            var MATERIALS = JsonConvert.DeserializeObject<Dictionary<string, Element>>(jsonData);
            if (ALL_ELEMENTS == null) ALL_ELEMENTS = new ConcurrentDictionary<int, Element>();

            foreach (var material in ALL_ELEMENTS.ToList())
                if (!MATERIALS.ContainsKey(material.Value.name))
                    ALL_ELEMENTS[material.Key] = EMPTY_ELEMENT;

            var counter = 0;
            var newByName = new Dictionary<string, Element>(MATERIALS.Count);
            foreach (var kv in MATERIALS)
            {
                counter++;
                kv.Value.name = kv.Key;
                kv.Value.id = counter;
                ALL_ELEMENTS[counter] = kv.Value;
                newByName[kv.Key] = kv.Value;
            }
            _elementsByName = newByName; // atomic reference swap

            if (current_element == null)
            {
                current_element = newByName.TryGetValue("water", out var w) ? w : ALL_ELEMENTS.First().Value;
            }
            else
            {
                current_element = newByName.TryGetValue(current_element.name, out var found)
                    ? found : ALL_ELEMENTS.First().Value;
            }

            foreach (var item in ALL_ELEMENTS)
            {
                if (item.Value.IsGenerator() && newByName.TryGetValue(item.Value.generatesMaterial, out var genEl))
                    item.Value.generatedMaterialIds = new int[1] { genEl.id };
            }
        }

        public void SetupFilewatch()
        {
            try
            {
                file_watcher = new FileSystemWatcher(Path.GetDirectoryName(Environment.CurrentDirectory) ?? "");
                file_watcher.Filter = Path.GetFileName(_ELEMENT_FILEPATH);
                file_watcher.NotifyFilter = NotifyFilters.LastWrite;
                file_watcher.Changed += Watcher_Changed;
                file_watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            DateTime lastWriteTime = File.GetLastWriteTime(_ELEMENT_FILEPATH);
            if (lastWriteTime != element_file_last_read_dt)
            {
                element_file_last_read_dt = lastWriteTime;
                LoadElements();
            }
        }

        int ROTATION_OFFSET = 0;

        public void UpdateAndDrawSandWorld()
        {
            // ===================================================================
            var actualMousePos = GetMousePosition();
            var screenMousePos = GetScreenToWorld2D(actualMousePos, world_camera);
            var newX = screenMousePos.X - screenMousePos.X % _GRID_PIXEL_SIZE;
            var newY = screenMousePos.Y - screenMousePos.Y % _GRID_PIXEL_SIZE;

            var mouseGridCoords = new MyVector2((int)newX, (int)newY);
            var mouseWorldCoords = GridToBitWorld(mouseGridCoords);

            var NeighboringMousePositions = GetNeighboringMousePositions(mouseGridCoords);

            var arrowMoveSpeed = 20;
            if (IsKeyDown(KEY_LEFT_CONTROL)) arrowMoveSpeed = 10;
            if (IsKeyDown(KEY_LEFT_SHIFT)) arrowMoveSpeed = 40;

            if (IsKeyDown(KEY_W)) world_camera.offset.Y += arrowMoveSpeed;
            if (IsKeyDown(KEY_A)) world_camera.offset.X += arrowMoveSpeed;
            if (IsKeyDown(KEY_S)) world_camera.offset.Y -= arrowMoveSpeed;
            if (IsKeyDown(KEY_D)) world_camera.offset.X -= arrowMoveSpeed;

            if (IsKeyPressed(KEY_LEFT)) { ROTATION_OFFSET++; Console.WriteLine(ROTATION_OFFSET); }
            if (IsKeyPressed(KEY_RIGHT)) { ROTATION_OFFSET--; Console.WriteLine(ROTATION_OFFSET); }

            if (ROTATION_OFFSET < 0) ROTATION_OFFSET = _validDirs.Length - 1;
            if (ROTATION_OFFSET >= _validDirs.Length) ROTATION_OFFSET = 0;

            if (IsKeyPressed(KEY_Q)) _FAST_DRAW_FLAG = !_FAST_DRAW_FLAG;
            if (IsKeyPressed(KEY_Z)) _DEBUG_FLAG = !_DEBUG_FLAG;
            if (IsKeyPressed(KEY_SPACE)) _PAUSE_FLAG = !_PAUSE_FLAG;
            if (IsKeyPressed(KEY_P)) _PARALLEL_FLAG = !_PARALLEL_FLAG;

            if (IsMouseButtonDown(MOUSE_BUTTON_MIDDLE))
            {
                Vector2 mouseDelta = GetMouseDelta();
                mouseDelta = Vector2Scale(mouseDelta, -1.0f / world_camera.zoom);
                world_camera.target = Vector2Add(world_camera.target, mouseDelta);
            }

            float mouseWheelMove = GetMouseWheelMove();
            if (mouseWheelMove != 0)
            {
                if (IsKeyDown(KEY_LEFT_CONTROL) || IsKeyDown(KEY_RIGHT_CONTROL))
                {
                    Vector2 mouseWorldPos = GetScreenToWorld2D(actualMousePos, world_camera);
                    world_camera.offset = actualMousePos;
                    world_camera.target = mouseWorldPos;
                    world_camera.zoom += mouseWheelMove * 0.01f;
                    if (world_camera.zoom < 0.05f) world_camera.zoom = 0.05f;
                }
                else
                {
                    var finalCursorSize = CURSOR_SIZE + (int)((mouseWheelMove * 4f) / 4) * 4;
                    CURSOR_SIZE = Math.Clamp(finalCursorSize, 4, 100);
                }
            }

            // =============================================
            var shouldIgnoreMouse = false;
            var isDisplacementMode = IsKeyDown(KEY_LEFT_SHIFT) || IsKeyDown(KEY_RIGHT_SHIFT);

            if (actualMousePos.X > _UI_WIDTH || actualMousePos.Y > _UI_HEIGHT)
            {
                shouldIgnoreMouse = false;
                HideCursor();
                if (IsMouseButtonDown(MOUSE_BUTTON_LEFT))
                {
                    wasMouseDown = true;
                    foreach (var item in NeighboringMousePositions)
                    {
                        var actualBitWorldPos = GridToBitWorld(item);
                        if (IsWorldCoordinate(actualBitWorldPos))
                        {
                            if (isDisplacementMode)
                            {
                                var centerX = actualBitWorldPos.X;
                                var centerY = actualBitWorldPos.Y;
                                var radius = CURSOR_SIZE / 2;

                                for (int dx = -(int)radius; dx <= radius; dx++)
                                {
                                    for (int dy = -(int)radius; dy <= radius; dy++)
                                    {
                                        int x = (int)centerX + dx;
                                        int y = (int)centerY + dy;

                                        if (!IsWorldCoordinate(x, y)) continue;

                                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                                        if (dist > radius) continue;

                                        var element = new_world[x, y];
                                        if (element.id == 0) continue;

                                        float pushScale = (radius - dist) / radius * 3;
                                        int pushX = (int)(dx * pushScale);
                                        int pushY = (int)(dy * pushScale);

                                        int targetX = Math.Max(0, Math.Min(x + pushX, _WORLD_WIDTH - 1));
                                        int targetY = Math.Max(0, Math.Min(y + pushY, _WORLD_HEIGHT - 1));

                                        SetNeighbor(new_world, targetX, targetY, element);
                                        SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                                    }
                                }
                            }
                            else
                            {
                                if (new_world[(int)actualBitWorldPos.X, (int)actualBitWorldPos.Y].id != 0) continue;

                                if (current_element.IsFrozen() || (actualBitWorldPos.X + actualBitWorldPos.Y) % 2 == 0)
                                {
                                    var newElement = current_element.SoftClone();
                                    newElement.correctionFactor = Rnd(1, 10) * 0.05f;
                                    SetNeighbor(new_world, actualBitWorldPos.X, actualBitWorldPos.Y, newElement);
                                }
                            }
                        }
                    }
                }
                else if (IsMouseButtonDown(MOUSE_BUTTON_RIGHT))
                {
                    foreach (var item in NeighboringMousePositions)
                    {
                        var actualBitWorldPos = GridToBitWorld(item);
                        if (IsWorldCoordinate(actualBitWorldPos))
                            SetNeighbor(new_world, actualBitWorldPos.X, actualBitWorldPos.Y, EMPTY_ELEMENT);
                    }
                }
                else if (wasMouseDown)
                {
                    wasMouseDown = false;
                    // Mark all chunks dirty after a mouse release so elements settle
                    for (int x = 0; x < _DIRTY_WORLD_WIDTH; x++)
                        for (int y = 0; y < _DIRTY_WORLD_HEIGHT; y++)
                            dirty_world_chunks[x, y] = true;
                }

                var cursorColor = isDisplacementMode ? new Color(255, 0, 255, 100) : new Color(0, 255, 0, 100);
                DrawCircleV(actualMousePos, CURSOR_SIZE, cursorColor);
            }
            else
            {
                shouldIgnoreMouse = true;
                ShowCursor();
            }

            bool shouldSimulate = !_PAUSE_FLAG;
            if (shouldSimulate)
            {
                for (int i = 0; i < speed_multiplier; i++)
                {
                    // Snapshot which chunks are dirty for this sub-step, then clear for next
                    old_dirty_world_chunks = (bool[,])dirty_world_chunks.Clone();
                    Array.Clear(dirty_world_chunks, 0, dirty_world_chunks.Length);
                    // _cellClaimed is cleared inside RunSimPass before each pass (solid + liquid).

                    // Pass 1: solids (sand, salt, etc.) — bottom to top so gravity works correctly
                    RunSimPass(solid: true);

                    // Pass 2: non-solids (liquids, gases) — bottom to top
                    RunSimPass(solid: false);
                }
            }

            if (GetRenderWidth() != target.texture.width || GetRenderHeight() != target.texture.height)
                target = LoadRenderTexture(GetRenderWidth(), GetRenderHeight());

            BeginTextureMode(target);
            int DRAW_CALLS = 0;

            DrawRectangleGradientEx(new Rectangle(0, 0, GetRenderWidth(), GetRenderHeight()), DARKBLUE, DARKPURPLE, DARKBLUE, DARKPURPLE);

            BeginMode2D(world_camera);

            var tmpOffset = 20f;
            DrawRectangleRec(new Rectangle(0 - tmpOffset, 0 - tmpOffset, _WORLD_WIDTH * _GRID_PIXEL_SIZE + tmpOffset + tmpOffset, _WORLD_HEIGHT * _GRID_PIXEL_SIZE + tmpOffset + tmpOffset), BLACK);
            DrawRectangleLinesEx(new Rectangle(0 - tmpOffset, 0 - tmpOffset, _WORLD_WIDTH * _GRID_PIXEL_SIZE + tmpOffset + tmpOffset, _WORLD_HEIGHT * _GRID_PIXEL_SIZE + tmpOffset + tmpOffset), tmpOffset, RAYWHITE);

            if (_FAST_DRAW_FLAG)
            {
                List<Rectangle> RENDER_CHUNKS = FindFastRenderChunks(true);
                foreach (var ch in RENDER_CHUNKS)
                {
                    var currType = new_world[(int)ch.X, (int)ch.Y];
                    var mat = currType;
                    Color clr = new Color(mat.color[0], mat.color[1], mat.color[2], 255);

                    DRAW_CALLS++;
                    DrawRectangle((int)ch.X * _GRID_PIXEL_SIZE, (int)ch.Y * _GRID_PIXEL_SIZE, (int)ch.width * _GRID_PIXEL_SIZE, (int)ch.height * _GRID_PIXEL_SIZE, clr);

                    if (_DEBUG_FLAG)
                    {
                        DRAW_CALLS++;
                        DrawRectangleLinesEx(new Rectangle((int)ch.X * _GRID_PIXEL_SIZE, (int)ch.Y * _GRID_PIXEL_SIZE, (int)ch.width * _GRID_PIXEL_SIZE, (int)ch.height * _GRID_PIXEL_SIZE), 2f, GREEN);
                    }
                }
            }
            else
            {
                for (int x = 0; x < _WORLD_WIDTH; x++)
                {
                    for (int y = 0; y < _WORLD_HEIGHT; y++)
                    {
                        var currElementType = new_world[x, y];
                        if (currElementType.id == 0) continue;

                        var mat = currElementType;
                        var currColor = new MyColor(mat.color[0], mat.color[1], mat.color[2]);
                        var finalColor = Helper.ChangeColorBrightness(currColor, mat.correctionFactor);
                        Color clr = new Color(finalColor.R, finalColor.G, finalColor.B, 255);

                        DRAW_CALLS++;
                        DrawRectangle(x * _GRID_PIXEL_SIZE, y * _GRID_PIXEL_SIZE, _GRID_PIXEL_SIZE, _GRID_PIXEL_SIZE, clr);
                    }
                }
            }

            if (_DEBUG_FLAG)
            {
                for (int x = 0; x < _DIRTY_WORLD_WIDTH; x++)
                {
                    for (int y = 0; y < _DIRTY_WORLD_HEIGHT; y++)
                    {
                        if (dirty_world_chunks[x, y])
                        {
                            DrawRectangleLinesEx(new Rectangle(x * (_GRID_PIXEL_SIZE * _DIRTY_CHUNK_SIZE), y * _GRID_PIXEL_SIZE * _DIRTY_CHUNK_SIZE, _GRID_PIXEL_SIZE * _DIRTY_CHUNK_SIZE, _GRID_PIXEL_SIZE * _DIRTY_CHUNK_SIZE), 10f, RED);
                        }
                    }
                }
            }

            if (!shouldIgnoreMouse && CheckCollisionPointRec(GetMousePosition(), new Rectangle(0, 0, GetScreenHeight(), GetScreenHeight())))
            {
                var radius = (CURSOR_SIZE / 2) * _GRID_PIXEL_SIZE;
                int xOffset = mouseGridCoords.X - radius;
                int yOffset = mouseGridCoords.Y - radius;
                int centerX = xOffset + radius;
                int centerY = yOffset + radius;

                var clrInsides = GetColorFromCache(0, 117, 44, 120);
                var clrBorders = GetColorFromCache(0, 117, 44, 190);

                if (IsMouseButtonDown(1))
                {
                    clrInsides = GetColorFromCache(230, 41, 55, 120);
                    clrBorders = GetColorFromCache(230, 41, 55, 190);
                }

                DrawCircle(centerX, centerY, radius, clrInsides);
                DrawRing(new Vector2(centerX, centerY), radius - radius * 0.05f, radius, 0, 360f, 36, clrBorders);

                radius = radius - radius / 2;
                DrawLineEx(new Vector2(centerX, centerY + radius), new Vector2(centerX, centerY - radius), CURSOR_SIZE, clrBorders);
                DrawLineEx(new Vector2(centerX - radius, centerY), new Vector2(centerX + radius, centerY), CURSOR_SIZE, clrBorders);
            }

            EndMode2D();
            EndTextureMode();

            BeginDrawing();
            DrawTextureRec(target.texture, new Rectangle(0, 0, (float)target.texture.width, (float)-target.texture.height), new Vector2(0, 0), WHITE);

            DrawRectangle(0, 0, _UI_WIDTH, _UI_HEIGHT, GetColorFromCache(0, 0, 0, 150));

            var pX = 30;
            var currY = 80;
            var ctrlWidth = 80;
            var blockSize = 20;

            foreach (var mtrl in ALL_ELEMENTS)
            {
                var rct = new Rectangle(pX, currY, blockSize, blockSize);

                var mouseIsHovering = actualMousePos.X >= rct.x && actualMousePos.X <= rct.x + rct.width
                    && actualMousePos.Y >= rct.Y && actualMousePos.Y <= rct.y + rct.height;

                DrawText(mtrl.Value.name.ToUpper(), rct.x + blockSize + 4, rct.y + blockSize / 4, 8f, (mouseIsHovering || current_element.id == mtrl.Key) ? RAYWHITE : GRAY);
                DrawRectangleRec(rct, new Color(mtrl.Value.color[0], mtrl.Value.color[1], mtrl.Value.color[2], 255));
                var rectColor = Helper.ChangeColorBrightness(new MyColor(mtrl.Value.color[0], mtrl.Value.color[1], mtrl.Value.color[2]), 0.4f);
                DrawRectangleLinesEx(rct, mouseIsHovering ? 4f : 2f, new Color(rectColor.R, rectColor.G, rectColor.B, 255));

                if (mouseIsHovering && IsMouseButtonDown(0))
                    current_element = mtrl.Value;

                currY += 22;
            }

            double tmpSpeed = GuiSlider(new Rectangle(pX + 20, 430, ctrlWidth - 20, 20), "SPEED", speed_multiplier.ToString() + "x", speed_multiplier, 1, 6);
            speed_multiplier = (int)tmpSpeed;

            pX = 10;
            DrawFPS(pX, 10);
            var boxCount = ELEMENT_COUNT;
            DrawText($"Count - {boxCount}", pX, 10 + 20, 10, YELLOW);
            DrawText($"Draws - {DRAW_CALLS}", pX, 10 + 30, 10, SKYBLUE);
            if (_PARALLEL_FLAG || _PAUSE_FLAG || _FAST_DRAW_FLAG || _DEBUG_FLAG)
            {
                DrawText($"Flags - " +
                    (_PARALLEL_FLAG ? "PARALLEL " : "") +
                    (_PAUSE_FLAG ? "PAUSE " : "") +
                    (_FAST_DRAW_FLAG ? "FAST_DRAW " : "") +
                    (_DEBUG_FLAG ? "DEBUG " : ""), pX, target.texture.height - 20, 10, GREEN);
            }

            if (_DEBUG_FLAG)
            {
                var screenHeight = GetScreenHeight();
                DrawText($"Mouse Ray - {mouseGridCoords.X} x {mouseGridCoords.Y}", pX, screenHeight - 30, 10, GREEN);
                DrawText($"Actual World Pos - {mouseWorldCoords.X} x {mouseWorldCoords.Y}", pX, screenHeight - 20, 10, RED);
            }
            EndDrawing();
        }

        // Run one simulation pass (solids or non-solids).
        // _cellClaimed is cleared at the start of EVERY pass so that cells vacated by
        // solids in Pass 1 can be freely filled by liquids in Pass 2.
        void RunSimPass(bool solid)
        {
            // Fresh claim slate — essential so Pass 2 (liquids) can enter cells vacated by Pass 1 (solids).
            Array.Clear(_cellClaimed, 0, _cellClaimed.Length);

            if (_PARALLEL_FLAG)
            {
                // Even columns first, then odd — adjacent columns never processed simultaneously,
                // which eliminates the most common parallel write conflicts.
                Parallel.For(0, (_WORLD_WIDTH + 1) / 2, xi =>
                {
                    int x = xi * 2;
                    for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                    {
                        var el = new_world[x, y];
                        if (el.id == 0 || el.solid != solid) continue;
                        (int dX, int dY) = GetDirtyWorldIndexes(x, y);
                        if (!old_dirty_world_chunks[dX, dY]) continue;
                        SimulateElement(x, y);
                    }
                });
                Parallel.For(0, _WORLD_WIDTH / 2, xi =>
                {
                    int x = xi * 2 + 1;
                    for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                    {
                        var el = new_world[x, y];
                        if (el.id == 0 || el.solid != solid) continue;
                        (int dX, int dY) = GetDirtyWorldIndexes(x, y);
                        if (!old_dirty_world_chunks[dX, dY]) continue;
                        SimulateElement(x, y);
                    }
                });
            }
            else
            {
                for (int x = 0; x < _WORLD_WIDTH; x++)
                {
                    for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                    {
                        var el = new_world[x, y];
                        if (el.id == 0 || el.solid != solid) continue;
                        (int dX, int dY) = GetDirtyWorldIndexes(x, y);
                        if (!old_dirty_world_chunks[dX, dY]) continue;
                        SimulateElement(x, y);
                    }
                }
            }
        }

        // Neighbour offsets for 8-directional checks (flaming spread, melting, etc.)
        static readonly (int dx, int dy)[] _neighbors8 =
        {
            (-1,-1),(0,-1),(1,-1),
            (-1, 0),       (1, 0),
            (-1, 1),(0, 1),(1, 1)
        };

        List<MyVector2> GetNeighboringMousePositions(MyVector2 mouse_pos)
        {
            var l = new List<MyVector2>();

            int radius = (CURSOR_SIZE * _GRID_PIXEL_SIZE) / 2;
            int radiusSquared = radius * radius;

            int xOffset = mouse_pos.X - radius;
            int yOffset = mouse_pos.Y - radius;
            int centerX = xOffset + radius;
            int centerY = yOffset + radius;

            for (int i = 0; i < CURSOR_SIZE; i++)
            {
                int x = xOffset + i * _GRID_PIXEL_SIZE;
                for (int j = 0; j < CURSOR_SIZE; j++)
                {
                    int y = yOffset + j * _GRID_PIXEL_SIZE;
                    var point = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
                    if (point <= radiusSquared)
                        l.Add(new MyVector2(x, y));
                }
            }

            return l;
        }

        MyVector2 GridToBitWorld(MyVector2 worldPos)
            => new MyVector2(worldPos.X / _GRID_PIXEL_SIZE, worldPos.Y / _GRID_PIXEL_SIZE);

        (int, int) GetDirtyWorldIndexes(int x, int y)
        {
            var tmpX = Math.Min(x / _DIRTY_CHUNK_SIZE, _DIRTY_WORLD_WIDTH - 1);
            var tmpY = Math.Min(y / _DIRTY_CHUNK_SIZE, _DIRTY_WORLD_HEIGHT - 1);
            return (tmpX, tmpY);
        }

        int ELEMENT_COUNT = 0;

        public void SetNeighbor(Element[,] my_world, int x, int y, Element elementType)
        {
            my_world[x, y] = elementType;

            (int dirtyX, int dirtyY) = GetDirtyWorldIndexes(x, y);

            // Mark the chunk and its immediate neighbours dirty so bordering elements wake up
            for (int tx = dirtyX - 1; tx <= dirtyX + 1; tx++)
                for (int ty = dirtyY - 1; ty <= dirtyY + 1; ty++)
                    if (tx >= 0 && tx < _DIRTY_WORLD_WIDTH && ty >= 0 && ty < _DIRTY_WORLD_HEIGHT)
                        dirty_world_chunks[tx, ty] = true;
        }

        (int, int) GetOffset(Directions dir)
        {
            switch (dir)
            {
                case Directions.NorthEast: return (1, -1);
                case Directions.NorthWest: return (-1, -1);
                case Directions.SouthEast: return (1, 1);
                case Directions.SouthWest: return (-1, 1);
                case Directions.North: return (0, -1);
                case Directions.South: return (0, 1);
                case Directions.West: return (-1, 0);
                case Directions.East: return (1, 0);
                default: return (0, 0);
            }
        }

        Dictionary<Directions, MyVector2> GetAllOffsets(MyVector2 elemPosition)
        {
            var ox = elemPosition.X;
            var oy = elemPosition.Y;
            return new Dictionary<Directions, MyVector2>
            {
                [Directions.NorthEast] = new MyVector2(ox + 1, oy - 1),
                [Directions.NorthWest] = new MyVector2(ox - 1, oy - 1),
                [Directions.SouthEast] = new MyVector2(ox + 1, oy + 1),
                [Directions.SouthWest] = new MyVector2(ox - 1, oy + 1),
                [Directions.North] = new MyVector2(ox, oy - 1),
                [Directions.South] = new MyVector2(ox, oy + 1),
                [Directions.West] = new MyVector2(ox - 1, oy),
                [Directions.East] = new MyVector2(ox + 1, oy),
            };
        }

        void SimulateElement(int x, int y)
        {
            // Claim this cell — prevents double-processing in parallel mode.
            if (Interlocked.CompareExchange(ref _cellClaimed[x, y], 1, 0) != 0)
                return;

            var currType = new_world[x, y];
            if (currType.id == 0) return;

            var currElement = currType;

            if (currElement.IsGenerator())
                currElement = GetElementById(currElement.generatedMaterialIds[0]);

            // ── Death chance ──────────────────────────────────────────────────────────────
            // Smoke, steam, fire, burning wood etc. disappear (or become another element) over time.
            if (currElement.deathChance > 0 && Rnd(0, 999) < currElement.deathChance)
            {
                var deathEl = string.IsNullOrEmpty(currElement.deathElement)
                    ? EMPTY_ELEMENT
                    : GetElementByName(currElement.deathElement);
                SetNeighbor(new_world, x, y, deathEl);
                return;
            }

            // ── Flaming: spread fire to adjacent flammable neighbours ─────────────────────
            // Burning elements (fire, lava, burning_wood, explosion) ignite flammables.
            // Also emits fire/smoke particles upward for visual effect.
            if (currElement.flaming)
            {
                // Flicker the brightness of burning elements
                currType.correctionFactor = Rnd(0, 40) * 0.01f - 0.1f;

                foreach (var (dx, dy) in _neighbors8)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!IsWorldCoordinate(nx, ny)) continue;
                    var neighbor = new_world[nx, ny];
                    if (neighbor.id == 0) continue;

                    // Ignite flammable neighbours via their own transformation rules
                    if (neighbor.transformations.TryGetValue(currElement.name, out var resultName)
                        && Rnd(0, 999) < 6)
                    {
                        var ignited = GetElementByName(resultName);
                        SetNeighbor(new_world, nx, ny, ignited);
                    }
                }

                // Emit fire/smoke upward from burning elements
                if (Rnd(0, 99) < 15)
                {
                    int ex = x + Rnd(-1, 1);
                    int ey = y - 1;
                    if (IsWorldCoordinate(ex, ey) && new_world[ex, ey].id == 0)
                    {
                        var particle = Rnd(0, 2) == 0
                            ? GetElementByName("smoke")
                            : GetElementByName("fire");
                        if (particle.id != 0)
                        {
                            var p = particle.SoftClone();
                            p.correctionFactor = Rnd(0, 20) * 0.05f - 0.3f;
                            SetNeighbor(new_world, ex, ey, p);
                        }
                    }
                }
            }

            // ── Melting: acid and lava dissolve meltable neighbours ───────────────────────
            if (currElement.melting)
            {
                foreach (var (dx, dy) in _neighbors8)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!IsWorldCoordinate(nx, ny)) continue;
                    var neighbor = new_world[nx, ny];
                    if (neighbor.id == 0 || !neighbor.meltable) continue;

                    if (Rnd(0, 999) < 4)
                    {
                        SetNeighbor(new_world, nx, ny, EMPTY_ELEMENT);
                        // Acid loses a cell when it melts something (conservation)
                        if (currElement.name == "acid" && Rnd(0, 1) == 0)
                        {
                            SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                            return;
                        }
                    }
                    break; // only process one neighbour per tick to keep it gradual
                }
            }

            if (currElement.IsFrozen()) return;
            if (currElement.behavior[0].Length == 0) return;

            int destCoordX = -1;
            int destCoordY = -1;

            if (!currElement.solid)
            {
                SimulateLiquid(x, y, currElement, ref destCoordX, ref destCoordY);
            }
            else
            {
                for (int beh = 0; beh < currElement.behavior.Count; beh++)
                {
                    var rnd = Rnd(0, currElement.behavior[beh].Length - 1);
                    var rawDir = currElement.behavior[beh][rnd];

                    int dirIndex = Array.IndexOf(_validDirs, rawDir);
                    if (dirIndex < 0) dirIndex = 0;
                    dirIndex = ((dirIndex + ROTATION_OFFSET) % _validDirs.Length + _validDirs.Length) % _validDirs.Length;
                    var randomDir = (Directions)_validDirs[dirIndex];

                    (int offsetX, int offsetY) = GetOffset(randomDir);
                    int candidateX = x + offsetX;
                    int candidateY = y + offsetY;

                    if (!IsWorldCoordinate(candidateX, candidateY)) continue;
                    if (new_world[candidateX, candidateY].id == currElement.id) continue;

                    if (TryMoveElement(x, y, candidateX, candidateY, currElement, out destCoordX, out destCoordY))
                        break;
                }
            }
        }

        // Returns true if the move/interaction succeeded, and sets destCoordX/Y to the final position.
        bool TryMoveElement(int x, int y, int newX, int newY, Element currElement, out int destCoordX, out int destCoordY)
        {
            destCoordX = x;
            destCoordY = y;

            if (!IsWorldCoordinate(newX, newY)) return false;

            var targetElement = new_world[newX, newY];

            // ── Empty cell: just move there ────────────────────────────────────────────────
            if (targetElement.id == 0)
            {
                // Claim the destination to prevent another thread from also moving here
                if (Interlocked.CompareExchange(ref _cellClaimed[newX, newY], 1, 0) != 0)
                    return false;

                SetNeighbor(new_world, newX, newY, currElement);
                SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                destCoordX = newX;
                destCoordY = newY;
                return true;
            }

            // ── Transformations ────────────────────────────────────────────────────────────
            // Each element transforms according to its OWN rules — fixes the bug where both
            // cells were incorrectly set to the same result (e.g. lava+water → two obsidians).
            bool currHasRule = currElement.transformations.ContainsKey(targetElement.name);
            bool tgtHasRule = targetElement.transformations.ContainsKey(currElement.name);

            if (currHasRule || tgtHasRule)
            {
                if (currHasRule)
                {
                    var result = GetElementByName(currElement.transformations[targetElement.name]);
                    SetNeighbor(new_world, x, y, result);
                }
                if (tgtHasRule)
                {
                    var result = GetElementByName(targetElement.transformations[currElement.name]);
                    SetNeighbor(new_world, newX, newY, result);
                }
                return true;
            }

            // ── Density-based displacement ─────────────────────────────────────────────────
            if (!targetElement.solid && !currElement.solid)
            {
                // Liquids/gases must not displace upward into heavier fluids
                if (currElement.density >= 20 && newY < y) return false;

                if (currElement.density > targetElement.density)
                {
                    // Claim destination before swapping
                    if (Interlocked.CompareExchange(ref _cellClaimed[newX, newY], 1, 0) != 0)
                        return false;

                    SetNeighbor(new_world, newX, newY, currElement);
                    SetNeighbor(new_world, x, y, targetElement);
                    destCoordX = newX;
                    destCoordY = newY;
                    return true;
                }
            }
            else if (currElement.solid && !targetElement.solid)
            {
                // Solid must not float upward through a liquid
                if (newY < y) return false;

                if (currElement.density > targetElement.density)
                {
                    if (Interlocked.CompareExchange(ref _cellClaimed[newX, newY], 1, 0) != 0)
                        return false;

                    SetNeighbor(new_world, newX, newY, currElement);
                    SetNeighbor(new_world, x, y, targetElement);
                    destCoordX = newX;
                    destCoordY = newY;
                    return true;
                }
            }

            return false;
        }

        void SimulateLiquid(int x, int y, Element currElement, ref int destCoordX, ref int destCoordY)
        {
            // Gases (density < 20) rise; everything else falls.
            int vert = currElement.density < 20 ? -1 : 1;

            // ── 1. Vertical fall — try up to `speed` steps, taking the farthest clear cell ─
            // This gives liquids fast fall while still respecting barriers correctly.
            int speed = Math.Max(1, currElement.GetRandomSpeed() - currElement.viscosity / 2);

            int fallY = y;
            for (int s = 1; s <= speed; s++)
            {
                int ty = y + vert * s;
                if (!IsWorldCoordinate(x, ty)) break;
                var t = new_world[x, ty];
                if (t.id != 0)
                {
                    // Can we displace this? (lighter non-solid)
                    if (!t.solid && currElement.density > t.density)
                        fallY = ty; // will displace; might try more but stop searching past this
                    break; // solid or same-density liquid blocks the path
                }
                fallY = ty; // empty — valid landing spot, keep looking deeper
            }

            if (fallY != y)
            {
                if (TryMoveElement(x, y, x, fallY, currElement, out destCoordX, out destCoordY)) return;
            }

            // ── 2. Diagonal (down-left / down-right one step) ─────────────────────────────
            bool goLeftFirst = Rnd(0, 1) == 0;
            int dA = goLeftFirst ? -1 : 1;
            int dB = -dA;

            if (TryMoveElement(x, y, x + dA, y + vert, currElement, out destCoordX, out destCoordY)) return;
            if (TryMoveElement(x, y, x + dB, y + vert, currElement, out destCoordX, out destCoordY)) return;

            // ── 3. Horizontal spread — one step in each direction, preferred side first ────
            // Liquids can only reach the immediately adjacent cell; they cannot jump over
            // obstacles.  Viscous liquids get a chance to skip the spread entirely.
            if (currElement.viscosity > 0 && Rnd(0, currElement.viscosity - 1) > 0) return;

            TrySpreadHorizontal(x, y, dA, currElement, ref destCoordX, ref destCoordY);
            if (destCoordX != x || destCoordY != y) return;
            TrySpreadHorizontal(x, y, dB, currElement, ref destCoordX, ref destCoordY);
        }

        // Try to move `currElement` one step horizontally in direction `xDir` (-1 or +1).
        // Walks cell-by-cell so it cannot jump over blockers, but can displace lighter liquids.
        void TrySpreadHorizontal(int x, int y, int xDir, Element currElement,
                                 ref int destCoordX, ref int destCoordY)
        {
            for (int s = 1; s <= 6; s++)   // search up to 6 cells wide per step
            {
                int tx = x + xDir * s;
                if (!IsWorldCoordinate(tx, y)) return;
                var tgt = new_world[tx, y];

                if (tgt.id == 0)
                {
                    // Empty — move here (nearest available)
                    TryMoveElement(x, y, tx, y, currElement, out destCoordX, out destCoordY);
                    return;
                }
                // Lighter non-solid: displace it, then stop
                if (!tgt.solid && currElement.density > tgt.density)
                {
                    TryMoveElement(x, y, tx, y, currElement, out destCoordX, out destCoordY);
                    return;
                }
                // Solid or same/denser fluid: can't pass, stop
                return;
            }
        }

        Color GetColorFromCache(int r, int g, int b, int a)
        {
            var rgba = (r << 24) + (g << 16) + (b << 8) + a;
            if (COLOR_CACHE == null) COLOR_CACHE = new ConcurrentDictionary<int, Color>();
            return COLOR_CACHE.GetOrAdd(rgba, _ => new Color(r, g, b, a));
        }

        List<Rectangle> FindFastRenderChunks(bool includeSinglePixel = false)
        {
            var world = new_world;
            int width = world.GetLength(0);
            int height = world.GetLength(1);
            bool[,] visited = new bool[width, height];
            List<Rectangle> rectangles = new();

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (world[x, y].id == 0 || visited[x, y]) continue;

                    int color = world[x, y].id;
                    int rectWidth = 1;
                    int rectHeight = 1;

                    while (x + rectWidth < width && world[x + rectWidth, y].id == color && !visited[x + rectWidth, y])
                        rectWidth++;

                    while (y + rectHeight < height)
                    {
                        bool canExpand = true;
                        for (int i = x; i < x + rectWidth; i++)
                            if (visited[i, y + rectHeight] || world[i, y + rectHeight].id != color) { canExpand = false; break; }
                        if (!canExpand) break;
                        rectHeight++;
                    }

                    for (int i = x; i < x + rectWidth; i++)
                        for (int j = y; j < y + rectHeight; j++)
                            visited[i, j] = true;

                    if (rectWidth == 1 && rectHeight == 1 && !includeSinglePixel) continue;

                    rectangles.Add(new Rectangle(x, y, rectWidth, rectHeight));
                }
            }

            return rectangles;
        }

        public bool IsWorldCoordinate(int x, int y)
            => x >= 0 && y >= 0 && x < _WORLD_WIDTH && y < _WORLD_HEIGHT;

        public bool IsWorldCoordinate(MyVector2 coords)
            => coords.X >= 0 && coords.Y >= 0 && coords.X < _WORLD_WIDTH && coords.Y < _WORLD_HEIGHT;
    }
}
