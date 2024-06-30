using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public FileSystemWatcher file_watcher;                              // monitor changes to elements.json file to live update elements
        public DateTime element_file_last_read_dt = DateTime.MinValue;      // last time elements.json was read

        public ConcurrentDictionary<int, Element> ALL_ELEMENTS;
        public readonly Element EMPTY_ELEMENT = new() { id = 0, name = "None" };

        public Element[,] new_world = new Element[_WORLD_WIDTH, _WORLD_HEIGHT];
        public bool[,] dirty_world_chunks;
        public bool[,] old_dirty_world_chunks;
        public int[,] world_color_map = new int[_WORLD_WIDTH, _WORLD_HEIGHT];

        public ConcurrentDictionary<int, Color> COLOR_CACHE;
        public Dictionary<int, Tuple<Rectangle, Material>> GUI_MATERIALS = new();
        public RenderTexture2D target;

        public SandWorld()
        {
            world_camera.target = new(_WORLD_WIDTH / 2.0f, _WORLD_HEIGHT / 2.0f);
            world_camera.offset = new(_WORLD_WIDTH / 2.0f, _WORLD_HEIGHT / 2.0f - 60);
            world_camera.rotation = 0.0f;
            world_camera.zoom = 0.08f;

            //SetTargetFPS(60);
            new_world = new Element[_WORLD_WIDTH, _WORLD_HEIGHT];

            _DIRTY_WORLD_WIDTH = _WORLD_WIDTH / _DIRTY_CHUNK_SIZE;
            _DIRTY_WORLD_HEIGHT = _WORLD_HEIGHT / _DIRTY_CHUNK_SIZE;

            dirty_world_chunks = new bool[_DIRTY_WORLD_WIDTH, _DIRTY_WORLD_HEIGHT];
            for (int x = 0; x < _DIRTY_WORLD_WIDTH; x++)
            {
                for (int y = 0; y < _DIRTY_WORLD_HEIGHT; y++)
                {
                    dirty_world_chunks[x, y] = false;
                }
            }

            world_color_map = new int[_WORLD_WIDTH, _WORLD_HEIGHT];

            for (int x = 0; x < _WORLD_WIDTH; x++)
            {
                for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                {
                    world_color_map[x, y] = GetRandomValue(1, 3);
                    new_world[x, y] = EMPTY_ELEMENT;
                }
            }

            LoadElements();
            SetupFilewatch();

        }


        private Element GetElementById(int id)
        {
            if (ALL_ELEMENTS == null)
            {
                ALL_ELEMENTS = new ConcurrentDictionary<int, Element>();
            }

            if (id == 0) return EMPTY_ELEMENT;

            return ALL_ELEMENTS[id];
        }

        public async void LoadElements()
        {
            var newPath = Path.GetTempPath() + Path.GetFileName(_ELEMENT_FILEPATH);
            File.Copy(_ELEMENT_FILEPATH, newPath, true);
            string jsonData;
            using (var fs = new FileStream(newPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using (var sr = new StreamReader(fs))
                {
                    jsonData = sr.ReadToEnd();
                }
            }
            if (string.IsNullOrEmpty(jsonData))
            {
                await Task.Delay(200);
                LoadElements();
                return;
            }

            var MATERIALS = JsonConvert.DeserializeObject<Dictionary<string, Element>>(jsonData);
            if (ALL_ELEMENTS == null)
            {
                ALL_ELEMENTS = new ConcurrentDictionary<int, Element>();
            }

            foreach (var material in ALL_ELEMENTS.ToList())
            {
                if (!MATERIALS.ContainsKey(material.Value.name))
                {
                    ALL_ELEMENTS[material.Key] = EMPTY_ELEMENT;
                }
            }
            var counter = 0;
            foreach (var kv in MATERIALS)
            {
                counter++;
                var newId = counter;
                ALL_ELEMENTS[newId] = kv.Value;
                kv.Value.name = kv.Key;
                kv.Value.id = newId;
            }

            if (current_element == null)
            {
                current_element = ALL_ELEMENTS.Where(am => am.Value.name.ToLower().Contains("sand")).First().Value;
            }
            else
            {
                var res = ALL_ELEMENTS.Where(am => am.Value.name == current_element.name).FirstOrDefault();
                if (res.Value != null)
                {
                    current_element = res.Value;
                }
                else
                {
                    current_element = ALL_ELEMENTS.First().Value;
                }
            }

            foreach (var item in ALL_ELEMENTS)
            {
                if (item.Value.IsGenerator())
                {
                    var id = ALL_ELEMENTS.Where(am => am.Value.name == item.Value.generatesMaterial).First().Value.id;
                    item.Value.generatedMaterialIds = new int[1] { id };
                }
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

            //if (IsKeyDown(KEY_LEFT)) world_camera.rotation -= 2;
            //if (IsKeyDown(KEY_RIGHT)) world_camera.rotation += 2;
            //world_camera.target
            if (IsKeyPressed(KEY_LEFT)) { ROTATION_OFFSET++; Console.WriteLine(ROTATION_OFFSET); }
            if (IsKeyPressed(KEY_RIGHT)) {ROTATION_OFFSET--; Console.WriteLine(ROTATION_OFFSET); }

            if (ROTATION_OFFSET < 0)
            {
                ROTATION_OFFSET = 9;
            }

            if (ROTATION_OFFSET > 9)
            {
                ROTATION_OFFSET = 0;
            }

            if (IsKeyPressed(KEY_Q))
            {
                _FAST_DRAW_FLAG = !_FAST_DRAW_FLAG;
            }

            if (IsKeyPressed(KEY_Z))
            {
                _DEBUG_FLAG = !_DEBUG_FLAG;
            }

            if (IsKeyPressed(KEY_SPACE))
            {
                _PAUSE_FLAG = !_PAUSE_FLAG;
            }

            if (IsKeyPressed(KEY_P))
            {
                _PARALLEL_FLAG = !_PARALLEL_FLAG;
            }

            if (IsMouseButtonDown(MOUSE_BUTTON_MIDDLE))
            {
                Vector2 mouseDelta = GetMouseDelta();
                mouseDelta = Vector2Scale(mouseDelta, -1.0f / world_camera.zoom);

                var newTarget = Vector2Add(world_camera.target, mouseDelta);
                var sss = 100f;

                Console.WriteLine(newTarget.ToString());
                world_camera.target = newTarget;

                if (newTarget.X < -sss || newTarget.X > _WORLD_WIDTH + sss)
                {

                }
                else
                {
                }
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
                    if (world_camera.zoom < 0.05f)
                        world_camera.zoom = 0.05f;
                }
                else
                {
                    var finalCursorSize = CURSOR_SIZE + (int)((mouseWheelMove * 4f) / 4) * 4;
                    if (finalCursorSize < 4)
                    {
                        finalCursorSize = 4;
                    }
                    else if (finalCursorSize > 100)
                    {
                        finalCursorSize = 100;
                    }

                    CURSOR_SIZE = finalCursorSize;
                }
            }

            // =============================================
            var shouldIgnoreMouse = false;

            if (actualMousePos.X > _UI_WIDTH || actualMousePos.Y > _UI_HEIGHT)
            {
                shouldIgnoreMouse = false;
                HideCursor();
                if (IsMouseButtonDown(MOUSE_BUTTON_LEFT))
                {
                    foreach (var item in NeighboringMousePositions)
                    {
                        var actualBitWorldPos = GridToBitWorld(item);
                        if (IsWorldCoordinate(actualBitWorldPos))
                        {
                            if (new_world[(int)actualBitWorldPos.X, (int)actualBitWorldPos.Y].id != 0) continue;

                            if (current_element.IsFrozen() || (actualBitWorldPos.X + actualBitWorldPos.Y) % 2 == 0)
                            {
                                var newElement = current_element.SoftClone();

                                var colorStep = GetRandomValue(1, 5);

                                var tmp = colorStep * 0.10f;
                                newElement.correctionFactor = tmp;

                                SetNeighbor(new_world, actualBitWorldPos.X, actualBitWorldPos.Y, newElement);
                                //break;
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
                        {
                            SetNeighbor(new_world, actualBitWorldPos.X, actualBitWorldPos.Y, EMPTY_ELEMENT);
                        }
                    }
                }
            }
            else
            {
                shouldIgnoreMouse = true;
                ShowCursor();
            }

            //var currGetTime = GetTime();
            bool shouldSimulate = !_PAUSE_FLAG;// && currGetTime - last_update_time > update_every;
            if (shouldSimulate)
            {
                old_dirty_world_chunks = dirty_world_chunks.Clone() as bool[,];
                for (int i = 0; i < speed_multiplier; i++)
                {
                    dirty_world_chunks = new bool[_DIRTY_WORLD_WIDTH, _DIRTY_WORLD_HEIGHT];
                    for (int x = 0; x < _DIRTY_WORLD_WIDTH; x++)
                    {
                        for (int y = 0; y < _DIRTY_WORLD_HEIGHT; y++)
                        {
                            dirty_world_chunks[x, y] = false;
                        }
                    }

                    if (_PARALLEL_FLAG)
                    {
                        Parallel.For(0, _WORLD_WIDTH, (x) =>
                        {
                            for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                            {
                                SimulateElement(x, y);
                            }
                        });

                        //Parallel.For(0, _DIRTY_WORLD_WIDTH, (x) =>
                        //{
                        //    for (int y = _DIRTY_WORLD_HEIGHT - 1; y >= 0; y--)
                        //    {
                        //        //if (!old_dirty_world_chunks[x, y]) continue;

                        //        SimulateChunk(new_world, x, y);
                        //    }
                        //});
                    }
                    else
                    {
                        for (int x = 0; x < _WORLD_WIDTH; x++)
                        {
                            for (int y = _WORLD_HEIGHT - 1; y >= 0; y--)
                            {
                                SimulateElement(x, y);
                            }
                        }
                    }
                }

            }
            else
            {
                //Console.WriteLine("DO NOT UPDATE");
            }

            if (GetRenderWidth() != target.texture.width || GetRenderHeight() != target.texture.height)
            {
                target = LoadRenderTexture(GetRenderWidth(), GetRenderHeight());
            }


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

                        if (!mat.IsFrozen() && !mat.solid && GetRandomValue(1, 500) % 200 == 0)
                        {
                            //world_color_map[x, y] = GetRandomValue(1, 3);
                        }

                        DRAW_CALLS++;
                        DrawRectangle(x * _GRID_PIXEL_SIZE, y * _GRID_PIXEL_SIZE, _GRID_PIXEL_SIZE, _GRID_PIXEL_SIZE, clr);
                    }
                }
            }

            if (_DEBUG_FLAG)
            {
                int dirtyWidth;
                int dirtyHeight;
                (dirtyWidth, dirtyHeight) = GetDirtyWorldIndexes(_WORLD_WIDTH, _WORLD_HEIGHT);

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

            if (!shouldIgnoreMouse && CheckCollisionPointRec(GetMousePosition(), new Rectangle(0, 0, GetScreenHeight(), GetScreenHeight()))) ;
            {
                var radius = (CURSOR_SIZE / 2) * _GRID_PIXEL_SIZE;
                int xOffset = mouseGridCoords.X - radius;
                int yOffset = mouseGridCoords.Y - radius;

                // Calculate the center of the circle
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

            // ===================================

            DrawRectangle(0, 0, _UI_WIDTH, _UI_HEIGHT, (GetColorFromCache(
                0,
                0,
                0,
                150
            )));

            var pX = 30;
            var currY = 80;
            var ctrlWidth = 80;

            var blockSize = 20;
            foreach (var mtrl in ALL_ELEMENTS)
            {
                var rct = new Rectangle(pX, currY, blockSize, blockSize);

                var mouseIsHovering = false;
                if (actualMousePos.X >= rct.x && actualMousePos.X <= rct.x + rct.width
                    && actualMousePos.Y >= rct.Y && actualMousePos.Y <= rct.y + rct.height)
                {
                    mouseIsHovering = true;
                }

                DrawText(mtrl.Value.name.ToUpper(), rct.x + blockSize + 4, rct.y + blockSize / 4, 8f, (mouseIsHovering || current_element.id == mtrl.Key) ? RAYWHITE : GRAY);

                DrawRectangleRec(rct, new Color(mtrl.Value.color[0], mtrl.Value.color[1], mtrl.Value.color[2], 255));
                var rectColor = Helper.ChangeColorBrightness(new MyColor(mtrl.Value.color[0], mtrl.Value.color[1], mtrl.Value.color[2]), 0.4f);
                DrawRectangleLinesEx(rct, mouseIsHovering ? 4f : 2f, new Color(rectColor.R, rectColor.G, rectColor.B, 255));

                if (mouseIsHovering && IsMouseButtonDown(0))
                {
                    current_element = mtrl.Value;
                }

                currY += 22;
            }
            //GuiLabelButton(new Rectangle(pX + 0, 430, 12, 12), "0.5x");
            //GuiLabelButton(new Rectangle(pX + 30, 430, 12, 12), "1x");
            //GuiLabelButton(new Rectangle(pX + 60, 430, 12, 12), "2x");
            double tmpSpeed = GuiSlider(new Rectangle(pX + 20, 430, ctrlWidth - 20, 20), "SPEED", speed_multiplier.ToString() + "x", speed_multiplier, 1, 6);
            speed_multiplier = (int)tmpSpeed;
            //update_every = 0.016f * (1/speed_multiplier);


            pX = 10;

            DrawFPS(pX, 10);
            var boxCount = ELEMENT_COUNT;
            DrawText($"Count - {boxCount}", pX, 10 + 20, 10, YELLOW);
            DrawText($"Draws - {DRAW_CALLS}", pX, 10 + 30, 10, SKYBLUE);
            if (_PARALLEL_FLAG || _PAUSE_FLAG || _FAST_DRAW_FLAG || _DEBUG_FLAG)
            {
                DrawText($"Flags - " +
                    ((_PARALLEL_FLAG) ? "PARALLEL " : "") +
                    ((_PAUSE_FLAG) ? "PAUSE " : "") +
                    ((_FAST_DRAW_FLAG) ? "FAST_DRAW " : "") +
                    ((_DEBUG_FLAG) ? "DEBUG " : ""), pX, target.texture.height - 20, 10, GREEN);
            }


            if (_DEBUG_FLAG)
            {
                var screenHeight = GetScreenHeight();
                DrawText($"Mouse Ray - {mouseGridCoords.X} x {mouseGridCoords.Y}", pX, screenHeight - 30, 10, GREEN);
                DrawText($"Actual World Pos - {mouseWorldCoords.X} x {mouseWorldCoords.Y}", pX, screenHeight - 20, 10, RED);
            }
            EndDrawing();
        }


        List<MyVector2> GetNeighboringMousePositions(MyVector2 mouse_pos)
        {
            var l = new List<MyVector2>();

            int radius = (CURSOR_SIZE * _GRID_PIXEL_SIZE) / 2; // This will be the radius of your circle.
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

                    var point = ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY));
                    if (point <= radiusSquared)
                    {
                        var final = new MyVector2(x, y);
                        l.Add(final);
                    }
                }
            }

            return l;
        }

        MyVector2 GridToBitWorld(MyVector2 worldPos)
        {
            return new MyVector2(worldPos.X / _GRID_PIXEL_SIZE, worldPos.Y / _GRID_PIXEL_SIZE);
        }

        (int, int) GetDirtyWorldIndexes(int x, int y)
        {
            var tmpX = x / _DIRTY_CHUNK_SIZE;
            var tmpY = y / _DIRTY_CHUNK_SIZE;

            if (tmpX >= _DIRTY_WORLD_WIDTH) tmpX = _DIRTY_WORLD_WIDTH - 1;
            if (tmpY >= _DIRTY_WORLD_HEIGHT) tmpY = _DIRTY_WORLD_HEIGHT - 1;

            return (tmpX, tmpY);
        }
        int ELEMENT_COUNT = 0;
        public void SetNeighbor(Element[,] my_world, int x, int y, Element elementType)
        {
            //if (elementType.id == 0)
            //{
            //    ELEMENT_COUNT--;
            //}
            //else
            //{
            //    ELEMENT_COUNT++;
            //}

            my_world[x, y] = elementType;

            int dirtyX;
            int dirtyY;
            (dirtyX, dirtyY) = GetDirtyWorldIndexes(x, y);

            var actualDirtyX = dirtyX;
            var actualDirtyY = dirtyY;

            var offset = 1;
            for (int tmpX = actualDirtyX - offset; tmpX <= actualDirtyX + offset; tmpX++)
            {
                for (int tmpY = actualDirtyY - offset; tmpY <= actualDirtyY + offset; tmpY++)
                {
                    var validX = tmpX >= 0 && tmpX < _DIRTY_WORLD_WIDTH;
                    var validY = tmpY >= 0 && tmpY < _DIRTY_WORLD_HEIGHT;
                    if (validX && validY)
                    {
                        dirty_world_chunks[tmpX, tmpY] = true;
                    }
                }
            }


            //var range = 1;
            //var newX1 = Math.Max(x - range, 0);
            //var newX2 = Math.Min(x + range, _WORLD_WIDTH - 1);
            //var newY1 = Math.Max(y - range, 0);
            //var newY2 = Math.Min(y + range, _WORLD_HEIGHT - 1);

            //for (int i = newX1; i <= newX2; i++)
            //{
            //    for (int j = newY1; j <= newY2; j++)
            //    {
            //        dirty_world[i, j] = true;
            //    }
            //}
        }


        (int, int) GetOffset(Directions dir)
        {
            var offsetX = 0;
            var offsetY = 0;

            switch (dir)
            {
                case Directions.NorthEast:
                    offsetX = 1; // Move right
                    offsetY = -1; // Move up
                    break;
                case Directions.NorthWest:
                    offsetX = -1; // Move left
                    offsetY = -1; // Move up
                    break;
                case Directions.SouthEast:
                    offsetX = 1; // Move right
                    offsetY = 1; // Move down
                    break;
                case Directions.SouthWest:
                    offsetX = -1; // Move left
                    offsetY = 1; // Move down
                    break;
                case Directions.North:
                    offsetX = 0; // Stay in column
                    offsetY = -1; // Move up
                    break;
                case Directions.South:
                    offsetX = 0; // Stay in column
                    offsetY = 1; // Move down
                    break;
                case Directions.West:
                    offsetX = -1; // Move left
                    offsetY = 0; // Stay in row
                    break;
                case Directions.East:
                    offsetX = 1; // Move right
                    offsetY = 0; // Stay in row
                    break;
            }

            return (offsetX, offsetY);
        }

        Dictionary<Directions, MyVector2> GetAllOffsets(MyVector2 elemPosition)
        {
            var offsetX = elemPosition.X;
            var offsetY = elemPosition.Y;

            Dictionary<Directions, MyVector2> final = new();

            final[Directions.NorthEast] = new MyVector2(offsetX + 1, offsetY - 1);
            final[Directions.NorthWest] = new MyVector2(offsetX - 1, offsetY - 1);
            final[Directions.SouthEast] = new MyVector2(offsetX + 1, offsetY + 1);
            final[Directions.SouthWest] = new MyVector2(offsetX - 1, offsetY + 1);
            final[Directions.North] = new MyVector2(offsetX, offsetY - 1);
            final[Directions.South] = new MyVector2(offsetX, offsetY + 1);
            final[Directions.West] = new MyVector2(offsetX - 1, offsetY);
            final[Directions.East] = new MyVector2(offsetX + 1, offsetY);

            return final;
        }

        //void SimulateChunk(Element[,] my_world, int chunkX, int chunkY)
        //{
        //    Parallel.For(chunkX, _DIRTY_CHUNK_SIZE, (x) =>
        //    {
        //        for (int y = chunkY; y < chunkY + _DIRTY_CHUNK_SIZE;y++)
        //        {
        //            SimulateElement(my_world, x, y);
        //        }
        //    });
        //}
        void SimulateElement(int x, int y, int? repeats = null)
        {
            var currType = new_world[x, y];
            if (currType.id == 0) return;

            int dirtyX;
            int dirtyY;
            (dirtyX, dirtyY) = GetDirtyWorldIndexes(x, y);

            if (old_dirty_world_chunks[dirtyX, dirtyY] == false)
            {
                //dirty_world_chunks[dirtyX, dirtyY] = false;
                return;
            }

            //SetNeighbor(my_world, x, y, currType);

            var currElement = currType;
            var wasElementGeneratorBefore = false;
            var generatorFrequency = 1;
            var generatorType = -1;
            if (currElement.IsGenerator())
            {
                wasElementGeneratorBefore = true;
                generatorType = currElement.id;
                generatorFrequency = currElement.generatorFrequency;
                currElement = GetElementById(currElement.generatedMaterialIds[0]);
            }

            if (currElement.IsFrozen()) return;

            if (currElement.behavior[0].Length == 0) return;

            if (repeats == null)
            {
                repeats = currElement.solid ? 1 : 1;
            }

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
                    var rnd = GetRandomValue(0, currElement.behavior[beh].Length - 1);

                    var randomDirInt = currElement.behavior[beh][rnd]; ;

                    randomDirInt += ROTATION_OFFSET;

                    if (randomDirInt <= 0)
                    {
                        randomDirInt = 9;
                    }

                    if (randomDirInt > 9)
                    {
                        randomDirInt = 1;
                    }



                    var randomDir = (Directions)randomDirInt;
                    int offsetX, offsetY;
                    (offsetX, offsetY) = GetOffset(randomDir);

                    destCoordX = x + offsetX;
                    destCoordY = y + offsetY;
                    var validNewCoords = IsWorldCoordinate(destCoordX, destCoordY);

                    if (!validNewCoords)
                    {
                        destCoordX = -1;
                        destCoordY = -1;
                        continue;
                    }

                    var destMat = new_world[destCoordX, destCoordY];
                    if (destMat.id == currElement.id)
                    {
                        // you shouldn't be able to move
                        continue;
                    }

                    if (currElement.flaming && destMat.flammable)
                    {
                        SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                        SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(4));
                        break;
                    }

                    if (currElement.melting && destMat.meltable)
                    {
                        SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                        SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(5));
                        break;
                    }

                    if (currElement.deathChance > 0 && !wasElementGeneratorBefore)
                    {
                        if (GetRandomValue(1, currElement.deathChance) == 1)
                        {
                            SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                            break;
                        }
                    }

                    if (destMat.IsFrozen() && destMat.id != 0) return;

                    if (destMat.density < currElement.density)
                    {
                        var tmp = new_world[destCoordX, destCoordY];

                        if (wasElementGeneratorBefore)
                        {
                            SetNeighbor(new_world, x, y, GetElementById(generatorType));
                            if (GetRandomValue(1, generatorFrequency) % 2 == 0)
                            {
                                SetNeighbor(new_world, destCoordX, destCoordY, currElement);
                            }
                        }
                        else
                        {
                            SetNeighbor(new_world, x, y, tmp);
                            SetNeighbor(new_world, destCoordX, destCoordY, currElement);
                        }

                        break;
                    }
                }
            }

            var newTime = repeats - 1;

            if (destCoordX != -1 && newTime > 0)
            {
                SimulateElement(destCoordX, destCoordY, newTime);
            }
        }

        void SimulateLiquid(int x, int y, Element currElement, ref int destCoordX, ref int destCoordY)
        {
            // 1. Determine primary movement direction (up or down)
            int verticalDirection = currElement.behavior.Any(b => b.Contains(8)) ? -1 : 1;
            // -1 for up (North/8 is in behavior), 1 for down (default)

            // 2. Check directly in the primary direction 
            if (TryMoveElement(x, y, x, y + verticalDirection, currElement, out destCoordX, out destCoordY)) return;

            // 3. Spread sideways, checking for obstacles
            int maxSpreadDistance = 20;
            int initialDirection = GetRandomValue(0, 1) * 2 - 1; // -1 or 1 for left/right

            for (int spread = 1; spread <= maxSpreadDistance && destCoordX == -1; spread++)
            {
                int newX = x + initialDirection * spread;
                int newY = y + verticalDirection;

                // Check for obstacles in the path
                bool obstacleFound = false;
                for (int checkX = x + initialDirection; checkX != newX && !obstacleFound; checkX += initialDirection)
                {
                    if (!IsWorldCoordinate(checkX, newY) || new_world[checkX, newY].id != 0)
                    {
                        obstacleFound = true;
                    }
                }

                if (!obstacleFound && TryMoveElement(x, y, newX, newY, currElement, out destCoordX, out destCoordY))
                {
                    return;
                }
            }

            // 4. Move diagonally (opposite of the primary direction) - No obstacle check here
            for (int spread = 1; spread <= maxSpreadDistance && destCoordX == -1; spread++)
            {
                int newX = x + initialDirection * spread;
                int newY = y - verticalDirection; // Opposite of verticalDirection

                // Check for obstacles diagonally
                bool obstacleFound = false;
                int stepX = initialDirection;
                int stepY = -verticalDirection;

                for (int checkX = x + stepX, checkY = y + stepY;
                     checkX != newX && checkY != newY && !obstacleFound;
                     checkX += stepX, checkY += stepY)
                {
                    if (!IsWorldCoordinate(checkX, checkY) || new_world[checkX, checkY].id != 0)
                    {
                        obstacleFound = true;
                    }
                }

                if (!obstacleFound && TryMoveElement(x, y, newX, newY, currElement, out destCoordX, out destCoordY))
                {
                    return;
                }
            }
        }

        bool TryMoveElement(int x, int y, int newX, int newY, Element currElement, out int destCoordX, out int destCoordY)
        {
            destCoordX = -1;
            destCoordY = -1;
            if (!IsWorldCoordinate(newX, newY)) return false;

            var destElement = new_world[newX, newY];
            if (destElement.id == 0 || destElement.density < currElement.density)
            {
                SetNeighbor(new_world, newX, newY, currElement);
                SetNeighbor(new_world, x, y, destElement);
                destCoordX = newX;
                destCoordY = newY;
                return true;
            }
            return false;
        }
        void SimulateElement2(int x, int y, int? repeats = null)
        {
            var currElement = new_world[x, y];
            if (currElement.id == 0) return;

            int dirtyX;
            int dirtyY;
            (dirtyX, dirtyY) = GetDirtyWorldIndexes(x, y);

            if (old_dirty_world_chunks[dirtyX, dirtyY] == false)
            {
                //return;
            }

            if (currElement.IsFrozen()) return;

            if (currElement.behavior[0].Length == 0) return;

            int destCoordX = -1;
            int destCoordY = -1;

            var allOffsets = GetAllOffsets(new(x, y));

            bool didMove = false;
            for (int behaviorIndex = 0; behaviorIndex < currElement.behavior.Count; behaviorIndex++)
            {
                var behaviorDirections = currElement.behavior[behaviorIndex];


                int rndElement = 0;
                // our first pick should ALWAYS be the first one in the array
                if (behaviorIndex != 0)
                {
                    rndElement = GetRandomValue(0, currElement.behavior[behaviorIndex].Length - 1);
                }


                if (!currElement.solid)
                {
                    var foundNewCoords = false;

                    var rng = new Random();
                    var randomlyShuffledBehaviors = (int[])currElement.behavior[behaviorIndex].Clone();

                    rng.Shuffle(randomlyShuffledBehaviors);

                    // liquid, handle a bit differently
                    for (int ii = 0; ii < randomlyShuffledBehaviors.Length; ii++)
                    {
                        var currOffset = allOffsets[(Directions)randomlyShuffledBehaviors[ii]];
                        var validNewCoords = IsWorldCoordinate(currOffset.X, currOffset.Y);

                        if (validNewCoords)
                        {
                            destCoordX = currOffset.X;
                            destCoordY = currOffset.Y;

                            foundNewCoords = true;
                            break;
                        }
                    }

                    if (!foundNewCoords)
                    {
                        continue;
                    }
                }
                else
                {
                    int dirIndex = 0;
                    var validNewCoords = false;
                    do
                    {
                        Directions dir = (Directions)behaviorDirections[dirIndex];
                        var randomDir = dir;

                        int offsetX, offsetY;
                        (offsetX, offsetY) = GetOffset(randomDir);

                        destCoordX = x + offsetX;
                        destCoordY = y + offsetY;
                        validNewCoords = IsWorldCoordinate(destCoordX, destCoordY);



                        dirIndex++;
                    } while (dirIndex < currElement.behavior[behaviorIndex].Length && !validNewCoords);

                    if (!validNewCoords)
                    {
                        destCoordX = -1;
                        destCoordY = -1;
                        continue;
                    }
                }


                var destMat = new_world[destCoordX, destCoordY];
                if (destMat.id == currElement.id)
                {
                    // you shouldn't be able to move
                    continue;
                }

                if (currElement.flaming && destMat.flammable)
                {
                    SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                    SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(4));
                    break;
                }

                if (currElement.melting && destMat.meltable)
                {
                    SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
                    SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(5));
                    break;
                }

            }
        }

        //void SimulateElement(int x, int y, int? repeats = null)
        //{
        //    var currType = new_world[x, y];
        //    if (currType.id == 0) return;

        //    int dirtyX;
        //    int dirtyY;
        //    (dirtyX, dirtyY) = GetDirtyWorldIndexes(x, y);

        //    if (old_dirty_world_chunks[dirtyX, dirtyY] == false)
        //    {
        //        //dirty_world_chunks[dirtyX, dirtyY] = false;
        //        return;
        //    }

        //    //SetNeighbor(my_world, x, y, currType);

        //    var currElement = currType;
        //    var wasElementGeneratorBefore = false;
        //    var generatorFrequency = 1;
        //    var generatorType = -1;
        //    if (currElement.IsGenerator())
        //    {
        //        wasElementGeneratorBefore = true;
        //        generatorType = currElement.id;
        //        generatorFrequency = currElement.generatorFrequency;
        //        currElement = GetElementById(currElement.generatedMaterialIds[0]);
        //    }

        //    if (currElement.IsFrozen()) return;

        //    if (currElement.behavior[0].Length == 0) return;

        //    if (repeats == null)
        //    {
        //        repeats = currElement.solid ? 1 : 1;
        //    }

        //    int destCoordX = -1;
        //    int destCoordY = -1;

        //    var allOffsets = GetAllOffsets(new(x, y));

        //    for (int behaviorIndex = 0; behaviorIndex < currElement.behavior.Count; behaviorIndex++)
        //    {
        //        int rndElement = 0;
        //        // our first pick should ALWAYS be the first one in the array
        //        if (behaviorIndex != 0)
        //        {
        //            rndElement = GetRandomValue(0, currElement.behavior[behaviorIndex].Length - 1);
        //        }
        //        var behaviorDirections = currElement.behavior[behaviorIndex];


        //        if (!currElement.solid)
        //        {
        //            var foundNewCoords = false;

        //            var rng = new Random();
        //            var randomlyShuffledBehaviors = (int[])currElement.behavior[behaviorIndex].Clone();

        //            rng.Shuffle(randomlyShuffledBehaviors);

        //            // liquid, handle a bit differently
        //            for (int ii = 0; ii < randomlyShuffledBehaviors.Length; ii++)
        //            {
        //                var currOffset = allOffsets[(Directions)randomlyShuffledBehaviors[ii]];
        //                var validNewCoords = IsWorldCoordinate(currOffset.X, currOffset.Y);

        //                if (validNewCoords)
        //                {
        //                    destCoordX = currOffset.X;
        //                    destCoordY = currOffset.Y;

        //                    foundNewCoords = true;
        //                    break;
        //                }
        //            }

        //            if (!foundNewCoords)
        //            {
        //                continue;
        //            }
        //        }
        //        else
        //        {


        //            int dirIndex = 0;
        //            var validNewCoords = false;
        //            do
        //            {
        //                Directions dir = (Directions)behaviorDirections[dirIndex];
        //                var randomDir = dir;

        //                int offsetX, offsetY;
        //                (offsetX, offsetY) = GetOffset(randomDir);

        //                destCoordX = x + offsetX;
        //                destCoordY = y + offsetY;
        //                validNewCoords = IsWorldCoordinate(destCoordX, destCoordY);



        //                dirIndex++;
        //            } while (dirIndex < currElement.behavior[behaviorIndex].Length && !validNewCoords);

        //            if (!validNewCoords)
        //            {
        //                destCoordX = -1;
        //                destCoordY = -1;
        //                continue;
        //            }
        //        }


        //        var destMat = new_world[destCoordX, destCoordY];
        //        if (destMat.id == currElement.id)
        //        {
        //            // you shouldn't be able to move
        //            continue;
        //        }

        //        if (currElement.flaming && destMat.flammable)
        //        {
        //            SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
        //            SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(4));
        //            break;
        //        }

        //        if (currElement.melting && destMat.meltable)
        //        {
        //            SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
        //            SetNeighbor(new_world, destCoordX, destCoordY, GetElementById(5));
        //            break;
        //        }

        //        if (currElement.deathChance > 0 && !wasElementGeneratorBefore)
        //        {
        //            if (GetRandomValue(1, currElement.deathChance) == 1)
        //            {
        //                SetNeighbor(new_world, x, y, EMPTY_ELEMENT);
        //                break;
        //            }
        //        }

        //        if (destMat.IsFrozen() && destMat.id != 0) return;

        //        if (destMat.density < currElement.density)
        //        {
        //            var tmp = new_world[destCoordX, destCoordY];

        //            if (wasElementGeneratorBefore)
        //            {
        //                SetNeighbor(new_world, x, y, GetElementById(generatorType));
        //                if (GetRandomValue(1, generatorFrequency) % 2 == 0)
        //                {
        //                    SetNeighbor(new_world, destCoordX, destCoordY, currElement);
        //                }
        //            }
        //            else
        //            {
        //                SetNeighbor(new_world, x, y, tmp);
        //                SetNeighbor(new_world, destCoordX, destCoordY, currElement);
        //            }

        //            break;
        //        }
        //    }

        //    var newTime = repeats - 1;

        //    if (destCoordX != -1 && newTime > 0)
        //    {
        //        SimulateElement(destCoordX, destCoordY, newTime);
        //    }

        //}


        Color GetColorFromCache(int r, int g, int b, int a)
        {
            var rgba = (r << 24) + (g << 16) + (b << 8) + a;
            if (COLOR_CACHE == null)
            {
                COLOR_CACHE = new ConcurrentDictionary<int, Color>();
            }

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
                    if (world[x, y].id == 0) continue;

                    if (!visited[x, y])
                    {
                        int color = world[x, y].id;
                        int rectWidth = 1;
                        int rectHeight = 1;

                        // expand horizontally
                        while (x + rectWidth < width && world[x + rectWidth, y].id == color && !visited[x + rectWidth, y])
                        {
                            rectWidth++;
                        }

                        // expand vertically
                        while (y + rectHeight < height)
                        {
                            bool canExpand = true;
                            for (int i = x; i < x + rectWidth; i++)
                            {
                                if (visited[i, y + rectHeight] || world[i, y + rectHeight].id != color)
                                {
                                    canExpand = false;
                                    break;
                                }
                            }
                            if (!canExpand) break;
                            rectHeight++;
                        }

                        for (int i = x; i < x + rectWidth; i++)
                        {
                            for (int j = y; j < y + rectHeight; j++)
                            {
                                visited[i, j] = true;
                            }
                        }


                        if (rectWidth == 1 && rectHeight == 1 && !includeSinglePixel)
                        {
                            continue;
                        }

                        rectangles.Add(new Rectangle(x, y, rectWidth, rectHeight));
                    }
                }
            }

            return rectangles;
        }

        public bool IsWorldCoordinate(int x, int y)
        {
            return x >= 0 && y >= 0 && x < _WORLD_WIDTH && y < _WORLD_HEIGHT;
        }
        public bool IsWorldCoordinate(MyVector2 coords)
        {
            return coords.X >= 0 && coords.Y >= 0 && coords.X < _WORLD_WIDTH && coords.Y < _WORLD_HEIGHT;
        }
    }
}
