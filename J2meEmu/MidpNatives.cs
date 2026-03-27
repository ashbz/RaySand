namespace J2meEmu;

static class MidpNatives
{
    public static void Register()
    {
        var R = NativeRegistry.RegisterMethod;

        RegisterMidlet(R);
        RegisterDisplay(R);
        RegisterDisplayable(R);
        RegisterCanvas(R);
        RegisterGameCanvas(R);
        RegisterGraphics(R);
        RegisterImage(R);
        RegisterFont(R);
        RegisterUI(R);
        RegisterGameApi(R);
        RegisterRecordStore(R);
        RegisterMedia(R);
        RegisterNokia(R);
        RegisterM3G(R);
        RegisterConnector(R);
    }

    static string? Str(JValue v) => (v.Ref as JavaObject)?.NativeData as string;
    static MidletHost.GraphicsContext? GetGfx(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.GraphicsContext;
    static MidletHost.ImageData? GetImg(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.ImageData;
    static MidletHost.FontData? GetFont(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.FontData;
    static MidletHost.SpriteData? GetSprite(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.SpriteData;
    static MidletHost.TiledLayerData? GetTiled(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.TiledLayerData;
    static MidletHost.LayerManagerData? GetLM(JValue v) => (v.Ref as JavaObject)?.NativeData as MidletHost.LayerManagerData;
    static J2meAudio.PlayerData? PD(JValue v) => (v.Ref as JavaObject)?.NativeData as J2meAudio.PlayerData;
    static T? N<T>(JValue v) where T : class => (v.Ref as JavaObject)?.NativeData as T;
    static void SetNative(JValue v, object data) { if (v.Ref is JavaObject obj) obj.NativeData = data; }

    // ── MIDlet ────────────────────────────────────────────────────
    static void RegisterMidlet(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/midlet/MIDlet", "<init>", "()V", (t, a) =>
        {
            t.Loader.Host!.MidletObject = a[0].Ref as JavaObject;
            return JValue.Null;
        });
        R("javax/microedition/midlet/MIDlet", "getAppProperty", "(Ljava/lang/String;)Ljava/lang/String;", (t, a) =>
        {
            string? key = Str(a[1]);
            if (key != null && t.Loader.ManifestProps.TryGetValue(key, out string? val))
                return JValue.OfRef(t.Loader.CreateString(val));
            return JValue.Null;
        });
        R("javax/microedition/midlet/MIDlet", "notifyDestroyed", "()V", (t, _) =>
        {
            t.Loader.Host!.NotifyDestroyed();
            return JValue.Null;
        });
        R("javax/microedition/midlet/MIDlet", "notifyPaused", "()V", (_, _) => JValue.Null);
        R("javax/microedition/midlet/MIDlet", "resumeRequest", "()V", (_, _) => JValue.Null);
        R("javax/microedition/midlet/MIDlet", "platformRequest", "(Ljava/lang/String;)Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/midlet/MIDlet", "checkPermission", "(Ljava/lang/String;)I", (_, _) => JValue.OfInt(1));
        R("javax/microedition/midlet/MIDletStateChangeException", "<init>", "()V", (_, _) => JValue.Null);
        R("javax/microedition/midlet/MIDletStateChangeException", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
    }

    // ── Display ───────────────────────────────────────────────────
    static void RegisterDisplay(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/lcdui/Display", "getDisplay", "(Ljavax/microedition/midlet/MIDlet;)Ljavax/microedition/lcdui/Display;", (t, _) =>
        {
            t.Loader.Host!.EnsureDisplayObject(t);
            return JValue.OfRef(t.Loader.Host.DisplayObject);
        });
        R("javax/microedition/lcdui/Display", "setCurrent", "(Ljavax/microedition/lcdui/Displayable;)V", (t, a) =>
        {
            t.Loader.Host!.SetCurrent(a[1].Ref as JavaObject);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Display", "setCurrent", "(Ljavax/microedition/lcdui/Alert;Ljavax/microedition/lcdui/Displayable;)V", (t, a) =>
        {
            t.Loader.Host!.SetCurrent(a[2].Ref as JavaObject);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Display", "getCurrent", "()Ljavax/microedition/lcdui/Displayable;", (t, _) =>
            JValue.OfRef(t.Loader.Host!.CurrentDisplayable));
        R("javax/microedition/lcdui/Display", "isColor", "()Z", (_, _) => JValue.OfInt(1));
        R("javax/microedition/lcdui/Display", "numColors", "()I", (_, _) => JValue.OfInt(16777216));
        R("javax/microedition/lcdui/Display", "numAlphaLevels", "()I", (_, _) => JValue.OfInt(256));
        R("javax/microedition/lcdui/Display", "callSerially", "(Ljava/lang/Runnable;)V", (t, a) =>
        {
            var runnable = a[1].Ref as JavaObject;
            if (runnable != null)
            {
                var runMethod = runnable.Class.FindMethod("run", "()V");
                if (runMethod != null)
                {
                    try { t.Invoke(runMethod, new[] { JValue.OfRef(runnable) }); }
                    catch (Exception ex) { Log.Error($"callSerially error: {ex.Message}"); }
                }
            }
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Display", "vibrate", "(I)Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Display", "flashBacklight", "(I)Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Display", "getBestImageWidth", "(I)I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenWidth));
        R("javax/microedition/lcdui/Display", "getBestImageHeight", "(I)I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenHeight));
    }

    // ── Displayable ───────────────────────────────────────────────
    static void RegisterDisplayable(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/lcdui/Displayable", "<init>", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "getWidth", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenWidth));
        R("javax/microedition/lcdui/Displayable", "getHeight", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenHeight));
        R("javax/microedition/lcdui/Displayable", "isShown", "()Z", (_, _) => JValue.OfInt(1));
        R("javax/microedition/lcdui/Displayable", "setTitle", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "getTitle", "()Ljava/lang/String;", (t, _) =>
            JValue.OfRef(t.Loader.CreateString("")));
        R("javax/microedition/lcdui/Displayable", "addCommand", "(Ljavax/microedition/lcdui/Command;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "removeCommand", "(Ljavax/microedition/lcdui/Command;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "setCommandListener", "(Ljavax/microedition/lcdui/CommandListener;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "setTicker", "(Ljavax/microedition/lcdui/Ticker;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "getTicker", "()Ljavax/microedition/lcdui/Ticker;", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Displayable", "sizeChanged", "(II)V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/Screen", "<init>", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Screen", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
    }

    // ── Canvas ────────────────────────────────────────────────────
    static void RegisterCanvas(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/lcdui/Canvas", "<init>", "()V", (t, a) =>
        {
            t.Loader.Host!.RegisterCanvas(a[0].Ref as JavaObject);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Canvas", "getWidth", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenWidth));
        R("javax/microedition/lcdui/Canvas", "getHeight", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenHeight));
        R("javax/microedition/lcdui/Canvas", "repaint", "()V", (t, _) =>
        {
            t.Loader.Host!.RequestRepaint();
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Canvas", "repaint", "(IIII)V", (t, _) =>
        {
            t.Loader.Host!.RequestRepaint();
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Canvas", "serviceRepaints", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Canvas", "isShown", "()Z", (_, _) => JValue.OfInt(1));
        R("javax/microedition/lcdui/Canvas", "setFullScreenMode", "(Z)V", (t, a) =>
        {
            t.Loader.Host!.FullScreen = a[1].Int != 0;
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Canvas", "hasPointerEvents", "()Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Canvas", "hasPointerMotionEvents", "()Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Canvas", "hasRepeatEvents", "()Z", (_, _) => JValue.OfInt(1));
        R("javax/microedition/lcdui/Canvas", "getKeyCode", "(I)I", (_, a) =>
            JValue.OfInt(MidletHost.GameActionToKeyCode(a[1].Int)));
        R("javax/microedition/lcdui/Canvas", "getKeyName", "(I)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(MidletHost.GetKeyName(a[1].Int))));
        R("javax/microedition/lcdui/Canvas", "getGameAction", "(I)I", (_, a) =>
            JValue.OfInt(MidletHost.KeyCodeToGameAction(a[1].Int)));
        R("javax/microedition/lcdui/Canvas", "isDoubleBuffered", "()Z", (_, _) => JValue.OfInt(1));
        R("javax/microedition/lcdui/Canvas", "keyPressed", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Canvas", "keyReleased", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Canvas", "keyRepeated", "(I)V", (_, _) => JValue.Null);
    }

    // ── GameCanvas ────────────────────────────────────────────────
    static void RegisterGameCanvas(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/lcdui/game/GameCanvas", "<init>", "(Z)V", (t, a) =>
        {
            t.Loader.Host!.RegisterCanvas(a[0].Ref as JavaObject);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/game/GameCanvas", "getGraphics", "()Ljavax/microedition/lcdui/Graphics;", (t, _) =>
            JValue.OfRef(t.Loader.Host!.GetScreenGraphics(t.Loader)));
        R("javax/microedition/lcdui/game/GameCanvas", "flushGraphics", "()V", (t, _) =>
        {
            t.Loader.Host!.FlushGraphics();
            return JValue.Null;
        });
        R("javax/microedition/lcdui/game/GameCanvas", "flushGraphics", "(IIII)V", (t, _) =>
        {
            t.Loader.Host!.FlushGraphics();
            return JValue.Null;
        });
        R("javax/microedition/lcdui/game/GameCanvas", "getKeyStates", "()I", (t, _) =>
            JValue.OfInt(t.Loader.Host!.GetKeyStates()));
        R("javax/microedition/lcdui/game/GameCanvas", "getWidth", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenWidth));
        R("javax/microedition/lcdui/game/GameCanvas", "getHeight", "()I", (t, _) => JValue.OfInt(t.Loader.Host!.ScreenHeight));
        R("javax/microedition/lcdui/game/GameCanvas", "paint", "(Ljavax/microedition/lcdui/Graphics;)V", (_, _) => JValue.Null);
    }

    // ── Graphics ──────────────────────────────────────────────────
    static void RegisterGraphics(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string G = "javax/microedition/lcdui/Graphics";

        R(G, "setColor", "(III)V", (_, a) =>
        {
            GetGfx(a[0])?.SetColor(a[1].Int, a[2].Int, a[3].Int);
            return JValue.Null;
        });
        R(G, "setColor", "(I)V", (_, a) =>
        {
            GetGfx(a[0])?.SetColor(a[1].Int);
            return JValue.Null;
        });
        R(G, "getColor", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.Color ?? 0));
        R(G, "getRedComponent", "()I", (_, a) => { var g = GetGfx(a[0]); return JValue.OfInt(g != null ? (g.Color >> 16) & 0xFF : 0); });
        R(G, "getGreenComponent", "()I", (_, a) => { var g = GetGfx(a[0]); return JValue.OfInt(g != null ? (g.Color >> 8) & 0xFF : 0); });
        R(G, "getBlueComponent", "()I", (_, a) => { var g = GetGfx(a[0]); return JValue.OfInt(g != null ? g.Color & 0xFF : 0); });

        R(G, "setClip", "(IIII)V", (_, a) =>
        {
            GetGfx(a[0])?.SetClip(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "clipRect", "(IIII)V", (_, a) =>
        {
            GetGfx(a[0])?.ClipRect(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "getClipX", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.ClipX ?? 0));
        R(G, "getClipY", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.ClipY ?? 0));
        R(G, "getClipWidth", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.ClipW ?? 0));
        R(G, "getClipHeight", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.ClipH ?? 0));

        R(G, "translate", "(II)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g != null) { g.TransX += a[1].Int; g.TransY += a[2].Int; }
            return JValue.Null;
        });
        R(G, "getTranslateX", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.TransX ?? 0));
        R(G, "getTranslateY", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.TransY ?? 0));

        R(G, "setStrokeStyle", "(I)V", (_, a) =>
        {
            var g = GetGfx(a[0]); if (g != null) g.StrokeStyle = a[1].Int;
            return JValue.Null;
        });
        R(G, "getStrokeStyle", "()I", (_, a) => JValue.OfInt(GetGfx(a[0])?.StrokeStyle ?? 0));

        R(G, "drawLine", "(IIII)V", (_, a) =>
        {
            GetGfx(a[0])?.DrawLine(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "drawRect", "(IIII)V", (_, a) =>
        {
            GetGfx(a[0])?.DrawRect(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "fillRect", "(IIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillRect(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "drawRoundRect", "(IIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.DrawRoundRect(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R(G, "fillRoundRect", "(IIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillRoundRect(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R(G, "drawArc", "(IIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.DrawArc(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R(G, "fillArc", "(IIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillArc(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R(G, "fillTriangle", "(IIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillTriangle(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });

        R(G, "drawString", "(Ljava/lang/String;III)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            string? s = Str(a[1]);
            if (g != null && s != null) g.DrawString(s, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "drawSubstring", "(Ljava/lang/String;IIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            string? s = Str(a[1]);
            if (g != null && s != null)
            {
                int off = Math.Max(0, a[2].Int);
                int len = Math.Min(a[3].Int, s.Length - off);
                if (len > 0) g.DrawString(s.Substring(off, len), a[4].Int, a[5].Int, a[6].Int);
            }
            return JValue.Null;
        });
        R(G, "drawChar", "(CIII)V", (_, a) =>
        {
            GetGfx(a[0])?.DrawString(((char)a[1].Int).ToString(), a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "drawChars", "([CIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g != null && a[1].Ref is JavaArray arr)
            {
                int off = a[2].Int, len = a[3].Int;
                string s = new string(arr.CharData, off, Math.Min(len, arr.Length - off));
                g.DrawString(s, a[4].Int, a[5].Int, a[6].Int);
            }
            return JValue.Null;
        });

        R(G, "drawImage", "(Ljavax/microedition/lcdui/Image;III)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            var img = GetImg(a[1]);
            if (g != null && img != null) g.DrawImage(img, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G, "drawRegion", "(Ljavax/microedition/lcdui/Image;IIIIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            var img = GetImg(a[1]);
            if (g != null && img != null)
                g.DrawRegion(img, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int, a[7].Int, a[8].Int, a[9].Int);
            return JValue.Null;
        });
        R(G, "drawRGB", "([IIIIIIIZ)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g != null && a[1].Ref is JavaArray arr)
                g.DrawRGB(arr.IntData, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int, a[7].Int);
            return JValue.Null;
        });
        R(G, "copyArea", "(IIIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.CopyArea(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int, a[7].Int);
            return JValue.Null;
        });

        R(G, "setFont", "(Ljavax/microedition/lcdui/Font;)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            var f = GetFont(a[1]);
            if (g != null && f != null) g.CurrentFont = f;
            return JValue.Null;
        });
        R(G, "getFont", "()Ljavax/microedition/lcdui/Font;", (t, a) =>
        {
            var g = GetGfx(a[0]);
            var f = g?.CurrentFont ?? new MidletHost.FontData();
            return JValue.OfRef(MidletHost.CreateFontObject(t.Loader, f.Face, f.Style, f.Size));
        });
    }

    // ── Image ─────────────────────────────────────────────────────
    static void RegisterImage(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string I = "javax/microedition/lcdui/Image";

        R(I, "createImage", "(II)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            int w = Math.Max(1, a[0].Int), h = Math.Max(1, a[1].Int);
            var pixels = new int[w * h];
            Array.Fill(pixels, unchecked((int)0xFFFFFFFF));
            var img = MidletHost.CreateImageObject(t.Loader, w, h, pixels);
            ((MidletHost.ImageData)img.NativeData!).Mutable = true;
            return JValue.OfRef(img);
        });
        R(I, "createImage", "(Ljavax/microedition/lcdui/Image;)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            var src = GetImg(a[0]);
            if (src == null) return JValue.Null;
            var pixels = (int[])src.Pixels.Clone();
            return JValue.OfRef(MidletHost.CreateImageObject(t.Loader, src.Width, src.Height, pixels));
        });
        R(I, "createImage", "(Ljava/lang/String;)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            string? path = Str(a[0]);
            return JValue.OfRef(t.Loader.Host!.LoadImageFromResource(t, path));
        });
        R(I, "createImage", "([BII)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            if (a[0].Ref is not JavaArray arr) return JValue.Null;
            return JValue.OfRef(t.Loader.Host!.LoadImageFromBytes(t.Loader, arr.ByteData, a[1].Int, a[2].Int));
        });
        R(I, "createImage", "(Ljava/io/InputStream;)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var data = ms.ToArray();
            return JValue.OfRef(t.Loader.Host!.LoadImageFromBytes(t.Loader, data, 0, data.Length));
        });
        R(I, "createImage", "(Ljavax/microedition/lcdui/Image;IIIII)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            var src = GetImg(a[0]);
            if (src == null) return JValue.Null;
            int sx = a[1].Int, sy = a[2].Int, sw = a[3].Int, sh = a[4].Int, transform = a[5].Int;
            var region = new int[sw * sh];
            for (int iy = 0; iy < sh; iy++)
                for (int ix = 0; ix < sw; ix++)
                {
                    int srcIdx = (sy + iy) * src.Width + (sx + ix);
                    if (srcIdx >= 0 && srcIdx < src.Pixels.Length)
                        region[iy * sw + ix] = src.Pixels[srcIdx];
                }
            var pixels = MidletHost.ApplyTransform(region, sw, sh, transform, out int outW, out int outH);
            return JValue.OfRef(MidletHost.CreateImageObject(t.Loader, outW, outH, pixels));
        });
        R(I, "createRGBImage", "([IIIZ)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            if (a[0].Ref is not JavaArray arr) return JValue.Null;
            int w = a[1].Int, h = a[2].Int;
            var pixels = new int[w * h];
            Array.Copy(arr.IntData, 0, pixels, 0, Math.Min(arr.IntData.Length, pixels.Length));
            return JValue.OfRef(MidletHost.CreateImageObject(t.Loader, w, h, pixels));
        });
        R(I, "getWidth", "()I", (_, a) => JValue.OfInt(GetImg(a[0])?.Width ?? 0));
        R(I, "getHeight", "()I", (_, a) => JValue.OfInt(GetImg(a[0])?.Height ?? 0));
        R(I, "isMutable", "()Z", (_, a) => JValue.OfInt(GetImg(a[0])?.Mutable == true ? 1 : 0));
        R(I, "getGraphics", "()Ljavax/microedition/lcdui/Graphics;", (t, a) =>
        {
            var img = GetImg(a[0]);
            if (img == null) return JValue.Null;
            return JValue.OfRef(MidletHost.CreateGraphicsForImage(t.Loader, img));
        });
        R(I, "getRGB", "([IIIIII)V", (_, a) =>
        {
            var img = GetImg(a[0]);
            if (img == null || a[1].Ref is not JavaArray arr) return JValue.Null;
            int off = a[2].Int, scanLen = a[3].Int, x = a[4].Int, y = a[5].Int, w = a[6].Int, h = a[7].Int;
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int srcIdx = (y + iy) * img.Width + (x + ix);
                    int dstIdx = off + iy * scanLen + ix;
                    if (srcIdx >= 0 && srcIdx < img.Pixels.Length && dstIdx >= 0 && dstIdx < arr.IntData.Length)
                        arr.IntData[dstIdx] = img.Pixels[srcIdx];
                }
            return JValue.Null;
        });
    }

    // ── Font ──────────────────────────────────────────────────────
    static void RegisterFont(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string F = "javax/microedition/lcdui/Font";

        R(F, "getDefaultFont", "()Ljavax/microedition/lcdui/Font;", (t, _) =>
            JValue.OfRef(MidletHost.CreateFontObject(t.Loader, 0, 0, 0)));
        R(F, "getFont", "(III)Ljavax/microedition/lcdui/Font;", (t, a) =>
            JValue.OfRef(MidletHost.CreateFontObject(t.Loader, a[0].Int, a[1].Int, a[2].Int)));
        R(F, "getHeight", "()I", (_, a) => JValue.OfInt(GetFont(a[0])?.Height ?? 12));
        R(F, "getBaselinePosition", "()I", (_, a) => JValue.OfInt((GetFont(a[0])?.Height ?? 12) - 2));
        R(F, "charWidth", "(C)I", (_, a) => JValue.OfInt(GetFont(a[0])?.CharWidth ?? 6));
        R(F, "charsWidth", "([CII)I", (_, a) => JValue.OfInt((GetFont(a[0])?.CharWidth ?? 6) * a[3].Int));
        R(F, "stringWidth", "(Ljava/lang/String;)I", (_, a) =>
            JValue.OfInt((GetFont(a[0])?.CharWidth ?? 6) * (Str(a[1])?.Length ?? 0)));
        R(F, "substringWidth", "(Ljava/lang/String;II)I", (_, a) =>
            JValue.OfInt((GetFont(a[0])?.CharWidth ?? 6) * a[3].Int));
        R(F, "getStyle", "()I", (_, a) => JValue.OfInt(GetFont(a[0])?.Style ?? 0));
        R(F, "getSize", "()I", (_, a) => JValue.OfInt(GetFont(a[0])?.Size ?? 0));
        R(F, "getFace", "()I", (_, a) => JValue.OfInt(GetFont(a[0])?.Face ?? 0));
        R(F, "isPlain", "()Z", (_, a) => JValue.OfInt(GetFont(a[0])?.Style == 0 ? 1 : 0));
        R(F, "isBold", "()Z", (_, a) => JValue.OfInt((GetFont(a[0])?.Style & 1) != 0 ? 1 : 0));
        R(F, "isItalic", "()Z", (_, a) => JValue.OfInt((GetFont(a[0])?.Style & 2) != 0 ? 1 : 0));
        R(F, "isUnderlined", "()Z", (_, a) => JValue.OfInt((GetFont(a[0])?.Style & 4) != 0 ? 1 : 0));
    }

    // ── UI: Form, Alert, List, Command, StringItem, TextField, etc
    static void RegisterUI(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/lcdui/Command", "<init>", "(Ljava/lang/String;II)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Command", "<init>", "(Ljava/lang/String;Ljava/lang/String;II)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Command", "getLabel", "()Ljava/lang/String;", (t, _) => JValue.OfRef(t.Loader.CreateString("")));
        R("javax/microedition/lcdui/Command", "getCommandType", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Command", "getPriority", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Command", "getLongLabel", "()Ljava/lang/String;", (t, _) => JValue.OfRef(t.Loader.CreateString("")));

        R("javax/microedition/lcdui/Form", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "<init>", "(Ljava/lang/String;[Ljavax/microedition/lcdui/Item;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "append", "(Ljavax/microedition/lcdui/Item;)I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Form", "append", "(Ljava/lang/String;)I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Form", "append", "(Ljavax/microedition/lcdui/Image;)I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Form", "delete", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "deleteAll", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "set", "(ILjavax/microedition/lcdui/Item;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "get", "(I)Ljavax/microedition/lcdui/Item;", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Form", "size", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Form", "setItemStateListener", "(Ljavax/microedition/lcdui/ItemStateListener;)V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/Alert", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "<init>", "(Ljava/lang/String;Ljava/lang/String;Ljavax/microedition/lcdui/Image;Ljavax/microedition/lcdui/AlertType;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "setTimeout", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "getTimeout", "()I", (_, _) => JValue.OfInt(-2));
        R("javax/microedition/lcdui/Alert", "setString", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "getString", "()Ljava/lang/String;", (t, _) => JValue.OfRef(t.Loader.CreateString("")));
        R("javax/microedition/lcdui/Alert", "setType", "(Ljavax/microedition/lcdui/AlertType;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "setImage", "(Ljavax/microedition/lcdui/Image;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Alert", "setIndicator", "(Ljavax/microedition/lcdui/Gauge;)V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/AlertType", "<init>", "()V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/List", "<init>", "(Ljava/lang/String;I)V", (_, a) =>
        {
            SetNative(a[0], new List<(string, object?)>());
            return JValue.Null;
        });
        R("javax/microedition/lcdui/List", "<init>", "(Ljava/lang/String;I[Ljava/lang/String;[Ljavax/microedition/lcdui/Image;)V", (_, a) =>
        {
            var items = new List<(string, object?)>();
            if (a[2].Ref is JavaArray arr)
                for (int i = 0; i < arr.Length; i++)
                    items.Add(((arr.RefData[i] as JavaObject)?.NativeData as string ?? "", null));
            SetNative(a[0], items);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/List", "append", "(Ljava/lang/String;Ljavax/microedition/lcdui/Image;)I", (_, a) =>
        {
            var list = (a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>;
            list?.Add((Str(a[1]) ?? "", a[2].Ref));
            return JValue.OfInt((list?.Count ?? 1) - 1);
        });
        R("javax/microedition/lcdui/List", "delete", "(I)V", (_, a) =>
        {
            var list = (a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>;
            int idx = a[1].Int;
            if (list != null && idx >= 0 && idx < list.Count) list.RemoveAt(idx);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/List", "deleteAll", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>)?.Clear();
            return JValue.Null;
        });
        R("javax/microedition/lcdui/List", "size", "()I", (_, a) =>
            JValue.OfInt(((a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>)?.Count ?? 0));
        R("javax/microedition/lcdui/List", "getString", "(I)Ljava/lang/String;", (t, a) =>
        {
            var list = (a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>;
            int idx = a[1].Int;
            if (list != null && idx >= 0 && idx < list.Count)
                return JValue.OfRef(t.Loader.CreateString(list[idx].Item1));
            return JValue.OfRef(t.Loader.CreateString(""));
        });
        R("javax/microedition/lcdui/List", "getSelectedIndex", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/List", "setSelectedIndex", "(IZ)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/List", "isSelected", "(I)Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/List", "set", "(ILjava/lang/String;Ljavax/microedition/lcdui/Image;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/List", "setFitPolicy", "(I)V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/Item", "<init>", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "getLabel", "()Ljava/lang/String;", (t, _) => JValue.OfRef(t.Loader.CreateString("")));
        R("javax/microedition/lcdui/Item", "setLabel", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "getLayout", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/Item", "setLayout", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "getPreferredWidth", "()I", (_, _) => JValue.OfInt(-1));
        R("javax/microedition/lcdui/Item", "getPreferredHeight", "()I", (_, _) => JValue.OfInt(-1));
        R("javax/microedition/lcdui/Item", "setPreferredSize", "(II)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "addCommand", "(Ljavax/microedition/lcdui/Command;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "setItemCommandListener", "(Ljavax/microedition/lcdui/ItemCommandListener;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "setDefaultCommand", "(Ljavax/microedition/lcdui/Command;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Item", "notifyStateChanged", "()V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/StringItem", "<init>", "(Ljava/lang/String;Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[2]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/StringItem", "<init>", "(Ljava/lang/String;Ljava/lang/String;I)V", (_, a) =>
        {
            SetNative(a[0], Str(a[2]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/StringItem", "getText", "()Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((a[0].Ref as JavaObject)?.NativeData as string ?? "")));
        R("javax/microedition/lcdui/StringItem", "setText", "(Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[1]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/StringItem", "setFont", "(Ljavax/microedition/lcdui/Font;)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/StringItem", "getFont", "()Ljavax/microedition/lcdui/Font;", (t, _) =>
            JValue.OfRef(MidletHost.CreateFontObject(t.Loader, 0, 0, 0)));

        R("javax/microedition/lcdui/TextField", "<init>", "(Ljava/lang/String;Ljava/lang/String;II)V", (_, a) =>
        {
            SetNative(a[0], Str(a[2]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/TextField", "getString", "()Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((a[0].Ref as JavaObject)?.NativeData as string ?? "")));
        R("javax/microedition/lcdui/TextField", "setString", "(Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[1]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/TextField", "getMaxSize", "()I", (_, _) => JValue.OfInt(256));
        R("javax/microedition/lcdui/TextField", "setMaxSize", "(I)I", (_, a) => JValue.OfInt(a[1].Int));
        R("javax/microedition/lcdui/TextField", "size", "()I", (_, a) =>
            JValue.OfInt(((a[0].Ref as JavaObject)?.NativeData as string ?? "").Length));
        R("javax/microedition/lcdui/TextField", "setConstraints", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/TextField", "getConstraints", "()I", (_, _) => JValue.OfInt(0));

        R("javax/microedition/lcdui/TextBox", "<init>", "(Ljava/lang/String;Ljava/lang/String;II)V", (_, a) =>
        {
            SetNative(a[0], Str(a[2]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/TextBox", "getString", "()Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((a[0].Ref as JavaObject)?.NativeData as string ?? "")));
        R("javax/microedition/lcdui/TextBox", "setString", "(Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[1]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/TextBox", "getMaxSize", "()I", (_, _) => JValue.OfInt(256));
        R("javax/microedition/lcdui/TextBox", "setMaxSize", "(I)I", (_, a) => JValue.OfInt(a[1].Int));
        R("javax/microedition/lcdui/TextBox", "size", "()I", (_, a) =>
            JValue.OfInt(((a[0].Ref as JavaObject)?.NativeData as string ?? "").Length));

        R("javax/microedition/lcdui/ChoiceGroup", "<init>", "(Ljava/lang/String;I)V", (_, a) =>
        {
            SetNative(a[0], new List<(string, object?)>());
            return JValue.Null;
        });
        R("javax/microedition/lcdui/ChoiceGroup", "<init>", "(Ljava/lang/String;I[Ljava/lang/String;[Ljavax/microedition/lcdui/Image;)V", (_, a) =>
        {
            SetNative(a[0], new List<(string, object?)>());
            return JValue.Null;
        });
        R("javax/microedition/lcdui/ChoiceGroup", "append", "(Ljava/lang/String;Ljavax/microedition/lcdui/Image;)I", (_, a) =>
        {
            var list = (a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>;
            list?.Add((Str(a[1]) ?? "", null));
            return JValue.OfInt((list?.Count ?? 1) - 1);
        });
        R("javax/microedition/lcdui/ChoiceGroup", "size", "()I", (_, a) =>
            JValue.OfInt(((a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>)?.Count ?? 0));
        R("javax/microedition/lcdui/ChoiceGroup", "getString", "(I)Ljava/lang/String;", (t, a) =>
        {
            var list = (a[0].Ref as JavaObject)?.NativeData as List<(string, object?)>;
            int idx = a[1].Int;
            if (list != null && idx >= 0 && idx < list.Count)
                return JValue.OfRef(t.Loader.CreateString(list[idx].Item1));
            return JValue.OfRef(t.Loader.CreateString(""));
        });
        R("javax/microedition/lcdui/ChoiceGroup", "getSelectedIndex", "()I", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/ChoiceGroup", "setSelectedIndex", "(IZ)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/ChoiceGroup", "isSelected", "(I)Z", (_, _) => JValue.OfInt(0));
        R("javax/microedition/lcdui/ChoiceGroup", "delete", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/ChoiceGroup", "deleteAll", "()V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/ChoiceGroup", "set", "(ILjava/lang/String;Ljavax/microedition/lcdui/Image;)V", (_, _) => JValue.Null);

        R("javax/microedition/lcdui/Ticker", "<init>", "(Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[1]) ?? "");
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Ticker", "getString", "()Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((a[0].Ref as JavaObject)?.NativeData as string ?? "")));
        R("javax/microedition/lcdui/Ticker", "setString", "(Ljava/lang/String;)V", (_, a) =>
        {
            SetNative(a[0], Str(a[1]) ?? "");
            return JValue.Null;
        });

        R("javax/microedition/lcdui/Gauge", "<init>", "(Ljava/lang/String;ZII)V", (_, a) =>
        {
            SetNative(a[0], a[4].Int);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Gauge", "getValue", "()I", (_, a) =>
            JValue.OfInt((a[0].Ref as JavaObject)?.NativeData is int v ? v : 0));
        R("javax/microedition/lcdui/Gauge", "setValue", "(I)V", (_, a) =>
        {
            SetNative(a[0], a[1].Int);
            return JValue.Null;
        });
        R("javax/microedition/lcdui/Gauge", "getMaxValue", "()I", (_, _) => JValue.OfInt(100));
        R("javax/microedition/lcdui/Gauge", "setMaxValue", "(I)V", (_, _) => JValue.Null);
        R("javax/microedition/lcdui/Gauge", "isInteractive", "()Z", (_, _) => JValue.OfInt(0));
    }

    // ── Game API: Sprite, TiledLayer, LayerManager, Layer ─────────
    static void RegisterGameApi(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string SP = "javax/microedition/lcdui/game/Sprite";
        const string TL = "javax/microedition/lcdui/game/TiledLayer";
        const string LM = "javax/microedition/lcdui/game/LayerManager";
        const string LY = "javax/microedition/lcdui/game/Layer";

        // Layer
        R(LY, "getX", "()I", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) return JValue.OfInt(sp.X);
            if (GetTiled(a[0]) is { } tl) return JValue.OfInt(tl.X);
            return JValue.OfInt(0);
        });
        R(LY, "getY", "()I", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) return JValue.OfInt(sp.Y);
            if (GetTiled(a[0]) is { } tl) return JValue.OfInt(tl.Y);
            return JValue.OfInt(0);
        });
        R(LY, "getWidth", "()I", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) return JValue.OfInt(sp.FrameW);
            if (GetTiled(a[0]) is { } tl) return JValue.OfInt(tl.GridCols * tl.TileW);
            return JValue.OfInt(0);
        });
        R(LY, "getHeight", "()I", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) return JValue.OfInt(sp.FrameH);
            if (GetTiled(a[0]) is { } tl) return JValue.OfInt(tl.GridRows * tl.TileH);
            return JValue.OfInt(0);
        });
        R(LY, "setVisible", "(Z)V", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) sp.Visible = a[1].Int != 0;
            if (GetTiled(a[0]) is { } tl) tl.Visible = a[1].Int != 0;
            return JValue.Null;
        });
        R(LY, "isVisible", "()Z", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) return JValue.OfInt(sp.Visible ? 1 : 0);
            if (GetTiled(a[0]) is { } tl) return JValue.OfInt(tl.Visible ? 1 : 0);
            return JValue.OfInt(1);
        });
        R(LY, "move", "(II)V", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) { sp.X += a[1].Int; sp.Y += a[2].Int; }
            if (GetTiled(a[0]) is { } tl) { tl.X += a[1].Int; tl.Y += a[2].Int; }
            return JValue.Null;
        });
        R(LY, "setPosition", "(II)V", (_, a) =>
        {
            if (GetSprite(a[0]) is { } sp) { sp.X = a[1].Int; sp.Y = a[2].Int; }
            if (GetTiled(a[0]) is { } tl) { tl.X = a[1].Int; tl.Y = a[2].Int; }
            return JValue.Null;
        });
        R(LY, "paint", "(Ljavax/microedition/lcdui/Graphics;)V", (_, a) =>
        {
            var g = GetGfx(a[1]);
            if (g == null) return JValue.Null;
            if (GetSprite(a[0]) is { } sp) sp.Paint(g);
            if (GetTiled(a[0]) is { } tl) tl.Paint(g);
            return JValue.Null;
        });

        // Sprite
        R(SP, "<init>", "(Ljavax/microedition/lcdui/Image;)V", (_, a) =>
        {
            var img = GetImg(a[1]);
            if (img != null) SetNative(a[0], new MidletHost.SpriteData(img, img.Width, img.Height));
            return JValue.Null;
        });
        R(SP, "<init>", "(Ljavax/microedition/lcdui/Image;II)V", (_, a) =>
        {
            var img = GetImg(a[1]);
            if (img != null) SetNative(a[0], new MidletHost.SpriteData(img, a[2].Int, a[3].Int));
            return JValue.Null;
        });
        R(SP, "<init>", "(Ljavax/microedition/lcdui/game/Sprite;)V", (_, a) =>
        {
            var src = GetSprite(a[1]);
            if (src != null)
            {
                var sd = new MidletHost.SpriteData(src.Source, src.FrameW, src.FrameH)
                {
                    X = src.X, Y = src.Y, RefPixelX = src.RefPixelX, RefPixelY = src.RefPixelY,
                    ColRectX = src.ColRectX, ColRectY = src.ColRectY, ColRectW = src.ColRectW, ColRectH = src.ColRectH,
                    Transform = src.Transform, CurrentFrame = src.CurrentFrame,
                    FrameSequence = (int[])src.FrameSequence.Clone(), Visible = src.Visible
                };
                SetNative(a[0], sd);
            }
            return JValue.Null;
        });
        R(SP, "setFrameSequence", "([I)V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            if (sp != null && a[1].Ref is JavaArray arr)
            {
                sp.FrameSequence = new int[arr.Length];
                Array.Copy(arr.IntData, sp.FrameSequence, arr.Length);
                sp.CurrentFrame = 0;
            }
            else if (sp != null && a[1].Ref == null)
            {
                int count = sp.RawFrameCount;
                sp.FrameSequence = new int[count];
                for (int i = 0; i < count; i++) sp.FrameSequence[i] = i;
                sp.CurrentFrame = 0;
            }
            return JValue.Null;
        });
        R(SP, "getFrameSequenceLength", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.FrameSequence.Length ?? 0));
        R(SP, "setFrame", "(I)V", (_, a) =>
        {
            var sp = GetSprite(a[0]); if (sp != null) sp.CurrentFrame = a[1].Int;
            return JValue.Null;
        });
        R(SP, "getFrame", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.CurrentFrame ?? 0));
        R(SP, "getRawFrameCount", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.RawFrameCount ?? 1));
        R(SP, "nextFrame", "()V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            if (sp != null) sp.CurrentFrame = (sp.CurrentFrame + 1) % sp.FrameSequence.Length;
            return JValue.Null;
        });
        R(SP, "prevFrame", "()V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            if (sp != null) sp.CurrentFrame = (sp.CurrentFrame - 1 + sp.FrameSequence.Length) % sp.FrameSequence.Length;
            return JValue.Null;
        });
        R(SP, "setTransform", "(I)V", (_, a) =>
        {
            var sp = GetSprite(a[0]); if (sp != null) sp.Transform = a[1].Int;
            return JValue.Null;
        });
        R(SP, "defineReferencePixel", "(II)V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            if (sp != null) { sp.RefPixelX = a[1].Int; sp.RefPixelY = a[2].Int; }
            return JValue.Null;
        });
        R(SP, "getRefPixelX", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.RefPixelX ?? 0));
        R(SP, "getRefPixelY", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.RefPixelY ?? 0));
        R(SP, "defineCollisionRectangle", "(IIII)V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            if (sp != null) { sp.ColRectX = a[1].Int; sp.ColRectY = a[2].Int; sp.ColRectW = a[3].Int; sp.ColRectH = a[4].Int; }
            return JValue.Null;
        });
        R(SP, "collidesWith", "(Ljavax/microedition/lcdui/game/Sprite;Z)Z", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            var other = GetSprite(a[1]);
            if (sp == null || other == null || !sp.Visible || !other.Visible) return JValue.OfInt(0);
            return JValue.OfInt(sp.CollidesBounds(
                other.X - other.RefPixelX + other.ColRectX, other.Y - other.RefPixelY + other.ColRectY,
                other.ColRectW, other.ColRectH) ? 1 : 0);
        });
        R(SP, "collidesWith", "(Ljavax/microedition/lcdui/game/TiledLayer;Z)Z", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            var tl = GetTiled(a[1]);
            if (sp == null || tl == null || !sp.Visible || !tl.Visible) return JValue.OfInt(0);
            return JValue.OfInt(sp.CollidesBounds(tl.X, tl.Y, tl.GridCols * tl.TileW, tl.GridRows * tl.TileH) ? 1 : 0);
        });
        R(SP, "collidesWith", "(Ljavax/microedition/lcdui/Image;IIZ)Z", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            var img = GetImg(a[1]);
            if (sp == null || img == null) return JValue.OfInt(0);
            return JValue.OfInt(sp.CollidesBounds(a[2].Int, a[3].Int, img.Width, img.Height) ? 1 : 0);
        });
        R(SP, "setImage", "(Ljavax/microedition/lcdui/Image;II)V", (_, a) =>
        {
            var sp = GetSprite(a[0]);
            var img = GetImg(a[1]);
            if (sp != null && img != null)
            {
                sp.Source = img;
                sp.FrameW = a[2].Int > 0 ? a[2].Int : img.Width;
                sp.FrameH = a[3].Int > 0 ? a[3].Int : img.Height;
                int count = sp.RawFrameCount;
                sp.FrameSequence = new int[count];
                for (int i = 0; i < count; i++) sp.FrameSequence[i] = i;
                sp.CurrentFrame = 0;
            }
            return JValue.Null;
        });
        R(SP, "getX", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.X ?? 0));
        R(SP, "getY", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.Y ?? 0));
        R(SP, "getWidth", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.FrameW ?? 0));
        R(SP, "getHeight", "()I", (_, a) => JValue.OfInt(GetSprite(a[0])?.FrameH ?? 0));
        R(SP, "setPosition", "(II)V", (_, a) =>
        {
            var sp = GetSprite(a[0]); if (sp != null) { sp.X = a[1].Int; sp.Y = a[2].Int; }
            return JValue.Null;
        });
        R(SP, "move", "(II)V", (_, a) =>
        {
            var sp = GetSprite(a[0]); if (sp != null) { sp.X += a[1].Int; sp.Y += a[2].Int; }
            return JValue.Null;
        });
        R(SP, "setVisible", "(Z)V", (_, a) =>
        {
            var sp = GetSprite(a[0]); if (sp != null) sp.Visible = a[1].Int != 0;
            return JValue.Null;
        });
        R(SP, "isVisible", "()Z", (_, a) => JValue.OfInt(GetSprite(a[0])?.Visible == true ? 1 : 0));
        R(SP, "paint", "(Ljavax/microedition/lcdui/Graphics;)V", (_, a) =>
        {
            var g = GetGfx(a[1]);
            if (g != null) GetSprite(a[0])?.Paint(g);
            return JValue.Null;
        });

        // TiledLayer
        R(TL, "<init>", "(IILjavax/microedition/lcdui/Image;II)V", (_, a) =>
        {
            var img = GetImg(a[3]);
            if (img != null)
                SetNative(a[0], new MidletHost.TiledLayerData(img, a[4].Int, a[5].Int, a[1].Int, a[2].Int));
            return JValue.Null;
        });
        R(TL, "setCell", "(III)V", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            if (tl != null)
            {
                int col = a[1].Int, row = a[2].Int, tile = a[3].Int;
                if (col >= 0 && col < tl.GridCols && row >= 0 && row < tl.GridRows)
                    tl.Cells[row, col] = tile;
            }
            return JValue.Null;
        });
        R(TL, "getCell", "(II)I", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            if (tl == null) return JValue.OfInt(0);
            int col = a[1].Int, row = a[2].Int;
            return JValue.OfInt(col >= 0 && col < tl.GridCols && row >= 0 && row < tl.GridRows ? tl.Cells[row, col] : 0);
        });
        R(TL, "fillCells", "(IIIII)V", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            if (tl != null)
            {
                int col = a[1].Int, row = a[2].Int, numCols = a[3].Int, numRows = a[4].Int, tile = a[5].Int;
                for (int r = row; r < row + numRows && r < tl.GridRows; r++)
                    for (int c = col; c < col + numCols && c < tl.GridCols; c++)
                        if (r >= 0 && c >= 0) tl.Cells[r, c] = tile;
            }
            return JValue.Null;
        });
        R(TL, "createAnimatedTile", "(I)I", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            return JValue.OfInt(tl?.CreateAnimatedTile(a[1].Int) ?? 0);
        });
        R(TL, "setAnimatedTile", "(II)V", (_, a) =>
        {
            GetTiled(a[0])?.SetAnimatedTile(a[1].Int, a[2].Int);
            return JValue.Null;
        });
        R(TL, "getAnimatedTile", "(I)I", (_, a) =>
            JValue.OfInt(GetTiled(a[0])?.GetAnimatedTile(a[1].Int) ?? 0));
        R(TL, "getColumns", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.GridCols ?? 0));
        R(TL, "getRows", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.GridRows ?? 0));
        R(TL, "getCellWidth", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.TileW ?? 0));
        R(TL, "getCellHeight", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.TileH ?? 0));
        R(TL, "paint", "(Ljavax/microedition/lcdui/Graphics;)V", (_, a) =>
        {
            var g = GetGfx(a[1]);
            if (g != null) GetTiled(a[0])?.Paint(g);
            return JValue.Null;
        });
        R(TL, "setPosition", "(II)V", (_, a) =>
        {
            var tl = GetTiled(a[0]); if (tl != null) { tl.X = a[1].Int; tl.Y = a[2].Int; }
            return JValue.Null;
        });
        R(TL, "move", "(II)V", (_, a) =>
        {
            var tl = GetTiled(a[0]); if (tl != null) { tl.X += a[1].Int; tl.Y += a[2].Int; }
            return JValue.Null;
        });
        R(TL, "getX", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.X ?? 0));
        R(TL, "getY", "()I", (_, a) => JValue.OfInt(GetTiled(a[0])?.Y ?? 0));
        R(TL, "getWidth", "()I", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            return JValue.OfInt(tl != null ? tl.GridCols * tl.TileW : 0);
        });
        R(TL, "getHeight", "()I", (_, a) =>
        {
            var tl = GetTiled(a[0]);
            return JValue.OfInt(tl != null ? tl.GridRows * tl.TileH : 0);
        });
        R(TL, "setVisible", "(Z)V", (_, a) =>
        {
            var tl = GetTiled(a[0]); if (tl != null) tl.Visible = a[1].Int != 0;
            return JValue.Null;
        });
        R(TL, "isVisible", "()Z", (_, a) => JValue.OfInt(GetTiled(a[0])?.Visible == true ? 1 : 0));

        // LayerManager
        R(LM, "<init>", "()V", (_, a) =>
        {
            SetNative(a[0], new MidletHost.LayerManagerData());
            return JValue.Null;
        });
        R(LM, "append", "(Ljavax/microedition/lcdui/game/Layer;)V", (_, a) =>
        {
            var lm = GetLM(a[0]);
            object? layer = (a[1].Ref as JavaObject)?.NativeData;
            if (lm != null && layer != null)
            {
                lm.Layers.Remove(layer);
                lm.Layers.Add(layer);
            }
            return JValue.Null;
        });
        R(LM, "insert", "(Ljavax/microedition/lcdui/game/Layer;I)V", (_, a) =>
        {
            var lm = GetLM(a[0]);
            object? layer = (a[1].Ref as JavaObject)?.NativeData;
            if (lm != null && layer != null)
            {
                lm.Layers.Remove(layer);
                int idx = Math.Clamp(a[2].Int, 0, lm.Layers.Count);
                lm.Layers.Insert(idx, layer);
            }
            return JValue.Null;
        });
        R(LM, "remove", "(Ljavax/microedition/lcdui/game/Layer;)V", (_, a) =>
        {
            var lm = GetLM(a[0]);
            object? layer = (a[1].Ref as JavaObject)?.NativeData;
            if (lm != null && layer != null) lm.Layers.Remove(layer);
            return JValue.Null;
        });
        R(LM, "getSize", "()I", (_, a) => JValue.OfInt(GetLM(a[0])?.Layers.Count ?? 0));
        R(LM, "setViewWindow", "(IIII)V", (_, a) =>
        {
            var lm = GetLM(a[0]);
            if (lm != null) { lm.ViewX = a[1].Int; lm.ViewY = a[2].Int; lm.ViewW = a[3].Int; lm.ViewH = a[4].Int; }
            return JValue.Null;
        });
        R(LM, "paint", "(Ljavax/microedition/lcdui/Graphics;II)V", (_, a) =>
        {
            var g = GetGfx(a[1]);
            var lm = GetLM(a[0]);
            if (g != null && lm != null) lm.Paint(g, a[2].Int, a[3].Int);
            return JValue.Null;
        });
    }

    // ── RecordStore (RMS) ─────────────────────────────────────────
    static readonly Dictionary<string, Dictionary<int, byte[]>> _recordStores = new();

    static void RegisterRecordStore(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string RS = "javax/microedition/rms/RecordStore";

        R(RS, "openRecordStore", "(Ljava/lang/String;Z)Ljavax/microedition/rms/RecordStore;", (t, a) =>
        {
            string? name = Str(a[0]);
            bool create = a[1].Int != 0;
            if (name == null) return JValue.Null;
            if (!_recordStores.ContainsKey(name))
            {
                if (!create) return JValue.Null;
                _recordStores[name] = new Dictionary<int, byte[]>();
            }
            var obj = new JavaObject(t.Loader.LoadClass(RS), name);
            return JValue.OfRef(obj);
        });
        R(RS, "openRecordStore", "(Ljava/lang/String;ZI)Ljavax/microedition/rms/RecordStore;", (t, a) =>
        {
            string? name = Str(a[0]);
            if (name == null) return JValue.Null;
            if (!_recordStores.ContainsKey(name)) _recordStores[name] = new Dictionary<int, byte[]>();
            return JValue.OfRef(new JavaObject(t.Loader.LoadClass(RS), name));
        });
        R(RS, "closeRecordStore", "()V", (_, _) => JValue.Null);
        R(RS, "deleteRecordStore", "(Ljava/lang/String;)V", (_, a) =>
        {
            string? name = Str(a[0]);
            if (name != null) _recordStores.Remove(name);
            return JValue.Null;
        });
        R(RS, "addRecord", "([BII)I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name == null || !_recordStores.TryGetValue(name, out var store)) return JValue.OfInt(0);
            if (a[1].Ref is not JavaArray arr) return JValue.OfInt(0);
            int off = a[2].Int, len = a[3].Int;
            var data = new byte[len];
            Array.Copy(arr.ByteData, off, data, 0, len);
            int id = store.Count > 0 ? store.Keys.Max() + 1 : 1;
            store[id] = data;
            return JValue.OfInt(id);
        });
        R(RS, "setRecord", "(I[BII)V", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name == null || !_recordStores.TryGetValue(name, out var store)) return JValue.Null;
            int id = a[1].Int;
            if (a[2].Ref is JavaArray arr)
            {
                int off = a[3].Int, len = a[4].Int;
                var data = new byte[len];
                Array.Copy(arr.ByteData, off, data, 0, len);
                store[id] = data;
            }
            return JValue.Null;
        });
        R(RS, "deleteRecord", "(I)V", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name != null && _recordStores.TryGetValue(name, out var store))
                store.Remove(a[1].Int);
            return JValue.Null;
        });
        R(RS, "getRecord", "(I)[B", (t, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name == null || !_recordStores.TryGetValue(name, out var store)) return JValue.Null;
            if (!store.TryGetValue(a[1].Int, out var data)) return JValue.Null;
            var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Byte, data.Length);
            Array.Copy(data, arr.ByteData, data.Length);
            return JValue.OfRef(arr);
        });
        R(RS, "getRecord", "(I[BI)I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name == null || !_recordStores.TryGetValue(name, out var store)) return JValue.OfInt(0);
            if (!store.TryGetValue(a[1].Int, out var data)) return JValue.OfInt(0);
            if (a[2].Ref is JavaArray arr)
            {
                int off = a[3].Int;
                int len = Math.Min(data.Length, arr.ByteData.Length - off);
                Array.Copy(data, 0, arr.ByteData, off, len);
            }
            return JValue.OfInt(data.Length);
        });
        R(RS, "getRecordSize", "(I)I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name != null && _recordStores.TryGetValue(name, out var store) && store.TryGetValue(a[1].Int, out var data))
                return JValue.OfInt(data.Length);
            return JValue.OfInt(0);
        });
        R(RS, "getNumRecords", "()I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name != null && _recordStores.TryGetValue(name, out var store))
                return JValue.OfInt(store.Count);
            return JValue.OfInt(0);
        });
        R(RS, "getNextRecordID", "()I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name != null && _recordStores.TryGetValue(name, out var store))
                return JValue.OfInt(store.Count > 0 ? store.Keys.Max() + 1 : 1);
            return JValue.OfInt(1);
        });
        R(RS, "getSize", "()I", (_, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            if (name != null && _recordStores.TryGetValue(name, out var store))
                return JValue.OfInt(store.Values.Sum(d => d.Length));
            return JValue.OfInt(0);
        });
        R(RS, "getSizeAvailable", "()I", (_, _) => JValue.OfInt(1024 * 1024));
        R(RS, "getLastModified", "()J", (_, _) => JValue.OfLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        R(RS, "getVersion", "()I", (_, _) => JValue.OfInt(1));
        R(RS, "getName", "()Ljava/lang/String;", (t, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            return JValue.OfRef(t.Loader.CreateString(name ?? ""));
        });
        R(RS, "enumerateRecords", "(Ljavax/microedition/rms/RecordFilter;Ljavax/microedition/rms/RecordComparator;Z)Ljavax/microedition/rms/RecordEnumeration;", (t, a) =>
        {
            string? name = (a[0].Ref as JavaObject)?.NativeData as string;
            var ids = new List<int>();
            if (name != null && _recordStores.TryGetValue(name, out var store))
                ids.AddRange(store.Keys.OrderBy(k => k));
            var enumObj = new JavaObject(t.Loader.LoadClass("javax/microedition/rms/RecordEnumeration"), new RecordEnum(name ?? "", ids));
            return JValue.OfRef(enumObj);
        });
        R(RS, "listRecordStores", "()[Ljava/lang/String;", (t, _) =>
        {
            var names = _recordStores.Keys.ToArray();
            if (names.Length == 0) return JValue.Null;
            var arr = t.Loader.CreateRefArray("java/lang/String", names.Length);
            for (int i = 0; i < names.Length; i++) arr.RefData[i] = t.Loader.CreateString(names[i]);
            return JValue.OfRef(arr);
        });

        R("javax/microedition/rms/RecordEnumeration", "hasNextElement", "()Z", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            return JValue.OfInt(e != null && e.Index < e.Ids.Count ? 1 : 0);
        });
        R("javax/microedition/rms/RecordEnumeration", "nextRecordId", "()I", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            if (e == null || e.Index >= e.Ids.Count) return JValue.OfInt(0);
            return JValue.OfInt(e.Ids[e.Index++]);
        });
        R("javax/microedition/rms/RecordEnumeration", "nextRecord", "()[B", (t, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            if (e == null || e.Index >= e.Ids.Count) return JValue.Null;
            int id = e.Ids[e.Index++];
            if (_recordStores.TryGetValue(e.StoreName, out var store) && store.TryGetValue(id, out var data))
            {
                var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Byte, data.Length);
                Array.Copy(data, arr.ByteData, data.Length);
                return JValue.OfRef(arr);
            }
            return JValue.Null;
        });
        R("javax/microedition/rms/RecordEnumeration", "numRecords", "()I", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            return JValue.OfInt(e?.Ids.Count ?? 0);
        });
        R("javax/microedition/rms/RecordEnumeration", "reset", "()V", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            if (e != null) e.Index = 0;
            return JValue.Null;
        });
        R("javax/microedition/rms/RecordEnumeration", "destroy", "()V", (_, _) => JValue.Null);
        R("javax/microedition/rms/RecordEnumeration", "hasPreviousElement", "()Z", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            return JValue.OfInt(e != null && e.Index > 0 ? 1 : 0);
        });
        R("javax/microedition/rms/RecordEnumeration", "previousRecordId", "()I", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as RecordEnum;
            if (e == null || e.Index <= 0) return JValue.OfInt(0);
            return JValue.OfInt(e.Ids[--e.Index]);
        });

        foreach (var exc in new[] { "javax/microedition/rms/RecordStoreException",
            "javax/microedition/rms/RecordStoreNotFoundException",
            "javax/microedition/rms/InvalidRecordIDException",
            "javax/microedition/rms/RecordStoreFullException" })
        {
            R(exc, "<init>", "()V", (_, _) => JValue.Null);
            R(exc, "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        }
    }

    // ── MMAPI (Media) ─────────────────────────────────────────────
    static void RegisterMedia(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string MGR = "javax/microedition/media/Manager";
        const string PL = "javax/microedition/media/Player";
        const string VC = "javax/microedition/media/control/VolumeControl";
        const string TC = "javax/microedition/media/control/ToneControl";

        R(MGR, "createPlayer", "(Ljava/io/InputStream;Ljava/lang/String;)Ljavax/microedition/media/Player;", (t, a) =>
        {
            var stream = (a[0].Ref as JavaObject)?.NativeData as Stream;
            string? type = Str(a[1]);
            var pd = J2meAudio.CreatePlayer(stream, type);
            var obj = new JavaObject(t.Loader.LoadClass(PL), pd);
            return JValue.OfRef(obj);
        });
        R(MGR, "createPlayer", "(Ljava/lang/String;)Ljavax/microedition/media/Player;", (t, a) =>
        {
            string? locator = Str(a[0]);
            J2meAudio.PlayerData pd;
            if (locator != null && !locator.StartsWith("capture://"))
            {
                var data = t.Loader.GetResource(locator.StartsWith("/") ? locator : "/" + locator);
                pd = J2meAudio.CreatePlayer(data ?? Array.Empty<byte>(), null);
            }
            else
                pd = J2meAudio.CreatePlayer((Stream?)null, null);
            return JValue.OfRef(new JavaObject(t.Loader.LoadClass(PL), pd));
        });
        R(MGR, "playTone", "(III)V", (_, a) =>
        {
            J2meAudio.PlayTone(a[0].Int, a[1].Int, a[2].Int);
            return JValue.Null;
        });
        R(MGR, "getSupportedContentTypes", "(Ljava/lang/String;)[Ljava/lang/String;", (t, _) =>
        {
            var types = new[] { "audio/wav", "audio/x-wav", "audio/mpeg", "audio/ogg", "audio/midi", "audio/x-tone-seq" };
            var arr = t.Loader.CreateRefArray("java/lang/String", types.Length);
            for (int i = 0; i < types.Length; i++) arr.RefData[i] = t.Loader.CreateString(types[i]);
            return JValue.OfRef(arr);
        });
        R(MGR, "getSupportedProtocols", "(Ljava/lang/String;)[Ljava/lang/String;", (t, _) =>
        {
            var arr = t.Loader.CreateRefArray("java/lang/String", 0);
            return JValue.OfRef(arr);
        });

        R(PL, "start", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Start(pd);
            return JValue.Null;
        });
        R(PL, "stop", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Stop(pd);
            return JValue.Null;
        });
        R(PL, "close", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Close(pd);
            return JValue.Null;
        });
        R(PL, "realize", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Realize(pd);
            return JValue.Null;
        });
        R(PL, "prefetch", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Prefetch(pd);
            return JValue.Null;
        });
        R(PL, "deallocate", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) { J2meAudio.Stop(pd); pd.State = 200; }
            return JValue.Null;
        });
        R(PL, "getState", "()I", (_, a) => JValue.OfInt(PD(a[0])?.State ?? 0));
        R(PL, "setLoopCount", "(I)V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) pd.LoopCount = a[1].Int;
            return JValue.Null;
        });
        R(PL, "getDuration", "()J", (_, _) => JValue.OfLong(-1));
        R(PL, "getMediaTime", "()J", (_, _) => JValue.OfLong(0));
        R(PL, "setMediaTime", "(J)J", (_, a) => JValue.OfLong(a[1].Long));
        R(PL, "getContentType", "()Ljava/lang/String;", (t, a) =>
        {
            var pd = PD(a[0]);
            return JValue.OfRef(t.Loader.CreateString(pd?.ContentType ?? "audio/unknown"));
        });
        R(PL, "addPlayerListener", "(Ljavax/microedition/media/PlayerListener;)V", (_, _) => JValue.Null);
        R(PL, "removePlayerListener", "(Ljavax/microedition/media/PlayerListener;)V", (_, _) => JValue.Null);
        R(PL, "getControl", "(Ljava/lang/String;)Ljavax/microedition/media/Control;", (t, a) =>
        {
            var pd = PD(a[0]);
            string? name = Str(a[1]);
            if (pd == null || name == null) return JValue.Null;
            if (name.Contains("VolumeControl") || name.Contains("volume"))
                return JValue.OfRef(new JavaObject(t.Loader.LoadClass(VC), pd));
            if (name.Contains("ToneControl") || name.Contains("tone"))
                return JValue.OfRef(new JavaObject(t.Loader.LoadClass(TC), pd));
            return JValue.Null;
        });
        R(PL, "getControls", "()[Ljavax/microedition/media/Control;", (t, a) =>
        {
            var pd = PD(a[0]);
            if (pd == null) return JValue.OfRef(t.Loader.CreateRefArray("javax/microedition/media/Control", 0));
            var vc = new JavaObject(t.Loader.LoadClass(VC), pd);
            var arr = t.Loader.CreateRefArray("javax/microedition/media/Control", 1);
            arr.RefData[0] = vc;
            return JValue.OfRef(arr);
        });

        R(VC, "setLevel", "(I)I", (_, a) =>
        {
            var pd = PD(a[0]);
            if (pd != null) J2meAudio.SetVolume(pd, a[1].Int);
            return JValue.OfInt(a[1].Int);
        });
        R(VC, "getLevel", "()I", (_, a) =>
        {
            var pd = PD(a[0]);
            return JValue.OfInt(pd != null ? (int)(pd.Volume * 100) : 100);
        });
        R(VC, "setMute", "(Z)V", (_, a) =>
        {
            var pd = PD(a[0]);
            if (pd != null) J2meAudio.SetVolume(pd, a[1].Int != 0 ? 0 : 100);
            return JValue.Null;
        });
        R(VC, "isMuted", "()Z", (_, a) =>
        {
            var pd = PD(a[0]);
            return JValue.OfInt(pd != null && pd.Volume < 0.01f ? 1 : 0);
        });

        R(TC, "setSequence", "([B)V", (_, _) => JValue.Null);

        R("javax/microedition/media/MediaException", "<init>", "()V", (_, _) => JValue.Null);
        R("javax/microedition/media/MediaException", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
    }

    // ── Nokia UI extensions ───────────────────────────────────────
    static void RegisterNokia(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("com/nokia/mid/ui/FullCanvas", "<init>", "()V", (t, a) =>
        {
            t.Loader.Host!.RegisterCanvas(a[0].Ref as JavaObject);
            t.Loader.Host.FullScreen = true;
            return JValue.Null;
        });

        R("com/nokia/mid/ui/DirectGraphics", "drawPixels", "([SZIIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g == null || a[1].Ref is not JavaArray arr) return JValue.Null;
            int off = a[3].Int, scanLen = a[4].Int, x = a[5].Int, y = a[6].Int, w = a[7].Int, h = a[8].Int;
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int idx = off + iy * scanLen + ix;
                    if (idx >= 0 && idx < arr.ShortData.Length)
                    {
                        int pixel = arr.ShortData[idx];
                        int r = ((pixel >> 11) & 0x1F) * 255 / 31;
                        int gv = ((pixel >> 5) & 0x3F) * 255 / 63;
                        int b = (pixel & 0x1F) * 255 / 31;
                        g.DrawPixel(x + ix, y + iy, unchecked((int)0xFF000000) | (r << 16) | (gv << 8) | b);
                    }
                }
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "drawPixels", "([IIZIIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g == null || a[1].Ref is not JavaArray arr) return JValue.Null;
            int off = a[3].Int, scanLen = a[4].Int, x = a[5].Int, y = a[6].Int, w = a[7].Int, h = a[8].Int;
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int idx = off + iy * scanLen + ix;
                    if (idx >= 0 && idx < arr.IntData.Length)
                        g.DrawPixel(x + ix, y + iy, arr.IntData[idx]);
                }
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "getPixels", "([SIIIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g == null || a[1].Ref is not JavaArray arr) return JValue.Null;
            int off = a[2].Int, scanLen = a[3].Int, x = a[4].Int, y = a[5].Int, w = a[6].Int, h = a[7].Int;
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int px = x + ix + g.TransX, py = y + iy + g.TransY;
                    int argb = 0;
                    if (px >= 0 && px < g.Width && py >= 0 && py < g.Height)
                        argb = g.Buffer[py * g.Width + px];
                    int r = (argb >> 16) & 0xFF, gv = (argb >> 8) & 0xFF, b = argb & 0xFF;
                    int rgb565 = ((r >> 3) << 11) | ((gv >> 2) << 5) | (b >> 3);
                    int idx = off + iy * scanLen + ix;
                    if (idx >= 0 && idx < arr.ShortData.Length) arr.ShortData[idx] = (short)rgb565;
                }
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "getPixels", "([IIIIIIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g == null || a[1].Ref is not JavaArray arr) return JValue.Null;
            int off = a[2].Int, scanLen = a[3].Int, x = a[4].Int, y = a[5].Int, w = a[6].Int, h = a[7].Int;
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int px = x + ix + g.TransX, py = y + iy + g.TransY;
                    int argb = 0;
                    if (px >= 0 && px < g.Width && py >= 0 && py < g.Height)
                        argb = g.Buffer[py * g.Width + px];
                    int idx = off + iy * scanLen + ix;
                    if (idx >= 0 && idx < arr.IntData.Length) arr.IntData[idx] = argb;
                }
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "setARGBColor", "(I)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            if (g != null) g.Color = a[1].Int & 0xFFFFFF;
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "getAlphaComponent", "()I", (_, _) => JValue.OfInt(255));
        R("com/nokia/mid/ui/DirectGraphics", "getNativePixelFormat", "()I", (_, _) => JValue.OfInt(888)); // TYPE_INT_888_RGB
        R("com/nokia/mid/ui/DirectGraphics", "drawTriangle", "(IIIIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillTriangle(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "fillTriangle", "(IIIIIIII)V", (_, a) =>
        {
            GetGfx(a[0])?.FillTriangle(a[1].Int, a[2].Int, a[3].Int, a[4].Int, a[5].Int, a[6].Int);
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "drawImage", "(Ljavax/microedition/lcdui/Image;IIII)V", (_, a) =>
        {
            var g = GetGfx(a[0]);
            var img = GetImg(a[1]);
            if (g != null && img != null) g.DrawImage(img, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R("com/nokia/mid/ui/DirectGraphics", "drawPolygon", "([III[IIII)V", (_, _) => JValue.Null);
        R("com/nokia/mid/ui/DirectGraphics", "fillPolygon", "([III[IIII)V", (_, _) => JValue.Null);

        R("com/nokia/mid/ui/DirectUtils", "getDirectGraphics", "(Ljavax/microedition/lcdui/Graphics;)Lcom/nokia/mid/ui/DirectGraphics;", (_, a) => a[1]);
        R("com/nokia/mid/ui/DirectUtils", "createImage", "(III)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            int w = Math.Max(1, a[0].Int), h = Math.Max(1, a[1].Int);
            int argb = a[2].Int;
            var pixels = new int[w * h];
            Array.Fill(pixels, argb);
            var img = MidletHost.CreateImageObject(t.Loader, w, h, pixels);
            ((MidletHost.ImageData)img.NativeData!).Mutable = true;
            return JValue.OfRef(img);
        });
        R("com/nokia/mid/ui/DirectUtils", "createImage", "([BII)Ljavax/microedition/lcdui/Image;", (t, a) =>
        {
            if (a[0].Ref is not JavaArray arr) return JValue.Null;
            return JValue.OfRef(t.Loader.Host!.LoadImageFromBytes(t.Loader, arr.ByteData, a[1].Int, a[2].Int));
        });

        R("com/nokia/mid/ui/DeviceControl", "flashLights", "(J)V", (_, _) => JValue.Null);
        R("com/nokia/mid/ui/DeviceControl", "setLights", "(II)V", (_, _) => JValue.Null);
        R("com/nokia/mid/ui/DeviceControl", "startVibra", "(IJ)V", (_, _) => JValue.Null);
        R("com/nokia/mid/ui/DeviceControl", "stopVibra", "()V", (_, _) => JValue.Null);

        // Nokia Sound API
        const string SND = "com/nokia/mid/sound/Sound";
        R(SND, "<init>", "([BI)V", (_, a) =>
        {
            if (a[1].Ref is JavaArray arr)
            {
                var data = new byte[arr.Length];
                Array.Copy(arr.ByteData, data, arr.Length);
                SetNative(a[0], J2meAudio.CreatePlayer(data, null));
            }
            return JValue.Null;
        });
        R(SND, "<init>", "(II)V", (_, a) =>
        {
            SetNative(a[0], J2meAudio.CreatePlayer(Array.Empty<byte>(), "audio/x-tone-seq"));
            return JValue.Null;
        });
        R(SND, "init", "([BI)V", (_, a) =>
        {
            if (a[1].Ref is JavaArray arr)
            {
                var data = new byte[arr.Length];
                Array.Copy(arr.ByteData, data, arr.Length);
                SetNative(a[0], J2meAudio.CreatePlayer(data, null));
            }
            return JValue.Null;
        });
        R(SND, "init", "(II)V", (_, a) =>
        {
            SetNative(a[0], J2meAudio.CreatePlayer(Array.Empty<byte>(), "audio/x-tone-seq"));
            return JValue.Null;
        });
        R(SND, "play", "(I)V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Start(pd);
            return JValue.Null;
        });
        R(SND, "stop", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Stop(pd);
            return JValue.Null;
        });
        R(SND, "release", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Close(pd);
            return JValue.Null;
        });
        R(SND, "resume", "()V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.Start(pd);
            return JValue.Null;
        });
        R(SND, "setGain", "(I)V", (_, a) =>
        {
            var pd = PD(a[0]); if (pd != null) J2meAudio.SetVolume(pd, a[1].Int);
            return JValue.Null;
        });
        R(SND, "getGain", "()I", (_, a) =>
        {
            var pd = PD(a[0]);
            return JValue.OfInt(pd != null ? (int)(pd.Volume * 100) : 100);
        });
        R(SND, "getState", "()I", (_, a) =>
        {
            var pd = PD(a[0]);
            if (pd == null) return JValue.OfInt(0);
            return JValue.OfInt(pd.Playing ? 4 : 1); // SOUND_PLAYING=4, SOUND_STOPPED=1
        });
        R(SND, "setSoundListener", "(Lcom/nokia/mid/sound/SoundListener;)V", (_, _) => JValue.Null);
    }

    // ── M3G (JSR-184 3D API) ──────────────────────────────────────
    static void RegisterM3G(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        const string PFX = "javax/microedition/m3g/";
        var renderer = new M3GRenderer();

        // Graphics3D
        string G3D = PFX + "Graphics3D";
        R(G3D, "getInstance", "()L" + G3D + ";", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass(G3D), renderer)));
        R(G3D, "bindTarget", "(Ljava/lang/Object;)V", (_, a) =>
        {
            var gfx = GetGfx(a[1]);
            if (gfx != null) renderer.BindTarget(gfx.Buffer, gfx.Width, gfx.Height);
            return JValue.Null;
        });
        R(G3D, "bindTarget", "(Ljava/lang/Object;Z)V", (_, a) =>
        {
            var gfx = GetGfx(a[1]);
            if (gfx != null) renderer.BindTarget(gfx.Buffer, gfx.Width, gfx.Height);
            return JValue.Null;
        });
        R(G3D, "bindTarget", "(Ljava/lang/Object;ZI)V", (_, a) =>
        {
            var gfx = GetGfx(a[1]);
            if (gfx != null) renderer.BindTarget(gfx.Buffer, gfx.Width, gfx.Height);
            return JValue.Null;
        });
        R(G3D, "releaseTarget", "()V", (_, _) => { renderer.ReleaseTarget(); return JValue.Null; });
        R(G3D, "setViewport", "(IIII)V", (_, a) =>
        {
            renderer.SetViewport(a[1].Int, a[2].Int, a[3].Int, a[4].Int);
            return JValue.Null;
        });
        R(G3D, "setCamera", "(L" + PFX + "Camera;L" + PFX + "Transform;)V", (_, a) =>
        {
            var cam = N<M3GCamera>(a[1]);
            var tf = N<M3GTransform>(a[2]);
            renderer.SetCamera(cam, tf?.Matrix);
            return JValue.Null;
        });
        R(G3D, "addLight", "(L" + PFX + "Light;L" + PFX + "Transform;)I", (_, a) =>
        {
            var light = N<M3GLight>(a[1]);
            var tf = N<M3GTransform>(a[2]);
            if (light != null) renderer.AddLight(light, tf?.Matrix);
            return JValue.OfInt(0);
        });
        R(G3D, "resetLights", "()V", (_, _) => { renderer.ResetLights(); return JValue.Null; });
        R(G3D, "clear", "(L" + PFX + "Background;)V", (_, a) =>
        {
            renderer.Clear(N<M3GBackground>(a[1]));
            return JValue.Null;
        });
        R(G3D, "render", "(L" + PFX + "World;)V", (_, a) =>
        {
            var world = N<M3GWorld>(a[1]);
            if (world != null) renderer.RenderWorld(world);
            return JValue.Null;
        });
        R(G3D, "render", "(L" + PFX + "VertexBuffer;L" + PFX + "IndexBuffer;L" + PFX + "Appearance;L" + PFX + "Transform;)V", (_, a) =>
        {
            renderer.RenderMeshImmediate(N<M3GVertexBuffer>(a[1]), N<M3GTriangleStripArray>(a[2]),
                N<M3GAppearance>(a[3]), N<M3GTransform>(a[4])?.Matrix);
            return JValue.Null;
        });
        R(G3D, "render", "(L" + PFX + "VertexBuffer;L" + PFX + "IndexBuffer;L" + PFX + "Appearance;L" + PFX + "Transform;I)V", (_, a) =>
        {
            renderer.RenderMeshImmediate(N<M3GVertexBuffer>(a[1]), N<M3GTriangleStripArray>(a[2]),
                N<M3GAppearance>(a[3]), N<M3GTransform>(a[4])?.Matrix);
            return JValue.Null;
        });
        R(G3D, "render", "(L" + PFX + "Node;L" + PFX + "Transform;)V", (_, _) => JValue.Null);
        R(G3D, "getProperties", "()Ljava/util/Hashtable;", (t, _) =>
        {
            var ht = new JavaObject(t.Loader.LoadClass("java/util/Hashtable"), new Dictionary<object, object?>());
            return JValue.OfRef(ht);
        });
        R(G3D, "setDepthRange", "(FF)V", (_, _) => JValue.Null);

        // Transform
        string TF = PFX + "Transform";
        R(TF, "<init>", "()V", (_, a) => { SetNative(a[0], new M3GTransform()); return JValue.Null; });
        R(TF, "<init>", "(L" + TF + ";)V", (_, a) =>
        {
            var src = N<M3GTransform>(a[1]);
            var t = new M3GTransform();
            if (src != null) Array.Copy(src.Matrix, t.Matrix, 16);
            SetNative(a[0], t);
            return JValue.Null;
        });
        R(TF, "setIdentity", "()V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null) Array.Copy(M3GMat.Identity(), t.Matrix, 16);
            return JValue.Null;
        });
        R(TF, "set", "([F)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr)
                for (int i = 0; i < 16 && i < arr.FloatData.Length; i++) t.Matrix[i] = arr.FloatData[i];
            return JValue.Null;
        });
        R(TF, "get", "([F)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr)
                for (int i = 0; i < 16 && i < arr.FloatData.Length; i++) arr.FloatData[i] = t.Matrix[i];
            return JValue.Null;
        });
        R(TF, "set", "(L" + TF + ";)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            var src = N<M3GTransform>(a[1]);
            if (t != null && src != null) Array.Copy(src.Matrix, t.Matrix, 16);
            return JValue.Null;
        });
        R(TF, "postMultiply", "(L" + TF + ";)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            var o = N<M3GTransform>(a[1]);
            if (t != null && o != null) t.Matrix = M3GMat.Multiply(t.Matrix, o.Matrix);
            return JValue.Null;
        });
        R(TF, "postTranslate", "(FFF)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null) t.Matrix = M3GMat.Multiply(t.Matrix, M3GMat.Translation(a[1].Float, a[2].Float, a[3].Float));
            return JValue.Null;
        });
        R(TF, "postScale", "(FFF)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null) t.Matrix = M3GMat.Multiply(t.Matrix, M3GMat.Scale(a[1].Float, a[2].Float, a[3].Float));
            return JValue.Null;
        });
        R(TF, "postRotate", "(FFFF)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null) t.Matrix = M3GMat.Multiply(t.Matrix, M3GMat.Rotation(a[1].Float, a[2].Float, a[3].Float, a[4].Float));
            return JValue.Null;
        });
        R(TF, "postRotateQuat", "(FFFF)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null) t.Matrix = M3GMat.Multiply(t.Matrix, M3GMat.Rotation(a[1].Float, a[2].Float, a[3].Float, a[4].Float));
            return JValue.Null;
        });
        R(TF, "invert", "()V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null && M3GMat.Invert(t.Matrix, out var inv)) t.Matrix = inv;
            return JValue.Null;
        });
        R(TF, "transpose", "()V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null)
            {
                var m = t.Matrix;
                for (int i = 0; i < 4; i++)
                    for (int j = i + 1; j < 4; j++)
                        (m[i * 4 + j], m[j * 4 + i]) = (m[j * 4 + i], m[i * 4 + j]);
            }
            return JValue.Null;
        });
        R(TF, "transform", "([F)V", (_, a) =>
        {
            var t = N<M3GTransform>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr && arr.FloatData.Length >= 4)
            {
                float x = arr.FloatData[0], y = arr.FloatData[1], z = arr.FloatData[2], w = arr.FloatData[3];
                M3GMat.TransformPoint(t.Matrix, x, y, z, out arr.FloatData[0], out arr.FloatData[1], out arr.FloatData[2], out arr.FloatData[3]);
            }
            return JValue.Null;
        });

        // Object3D base
        RegisterObject3D(R, PFX + "Object3D");

        // Transformable helpers
        RegisterM3GTransformable<M3GTransformable>(R, PFX + "Transformable");
        RegisterM3GTransformable<M3GNode>(R, PFX + "Node");
        RegisterM3GTransformable<M3GGroup>(R, PFX + "Group");
        RegisterM3GTransformable<M3GWorld>(R, PFX + "World");
        RegisterM3GTransformable<M3GCamera>(R, PFX + "Camera");
        RegisterM3GTransformable<M3GLight>(R, PFX + "Light");
        RegisterM3GTransformable<M3GMesh>(R, PFX + "Mesh");
        RegisterM3GTransformable<M3GTexture2D>(R, PFX + "Texture2D");

        // Node
        RegisterM3GNode(R, PFX + "Node");
        RegisterM3GNode(R, PFX + "Group");
        RegisterM3GNode(R, PFX + "World");
        RegisterM3GNode(R, PFX + "Camera");
        RegisterM3GNode(R, PFX + "Light");
        RegisterM3GNode(R, PFX + "Mesh");

        // Group
        RegisterM3GGroup(R, PFX + "Group");
        RegisterM3GGroup(R, PFX + "World");

        // World
        R(PFX + "World", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GWorld()); return JValue.Null; });
        R(PFX + "World", "getActiveCamera", "()L" + PFX + "Camera;", (t, a) =>
        {
            var w = N<M3GWorld>(a[0]);
            if (w?.ActiveCamera == null) return JValue.Null;
            return JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "Camera"), w.ActiveCamera));
        });
        R(PFX + "World", "setActiveCamera", "(L" + PFX + "Camera;)V", (_, a) =>
        {
            var w = N<M3GWorld>(a[0]);
            if (w != null) w.ActiveCamera = N<M3GCamera>(a[1]);
            return JValue.Null;
        });
        R(PFX + "World", "getBackground", "()L" + PFX + "Background;", (t, a) =>
        {
            var w = N<M3GWorld>(a[0]);
            if (w?.Background == null) return JValue.Null;
            return JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "Background"), w.Background));
        });
        R(PFX + "World", "setBackground", "(L" + PFX + "Background;)V", (_, a) =>
        {
            var w = N<M3GWorld>(a[0]);
            if (w != null) w.Background = N<M3GBackground>(a[1]);
            return JValue.Null;
        });

        // Camera
        R(PFX + "Camera", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GCamera()); return JValue.Null; });
        R(PFX + "Camera", "setPerspective", "(FFFF)V", (_, a) =>
        {
            N<M3GCamera>(a[0])?.SetPerspective(a[1].Float, a[2].Float, a[3].Float, a[4].Float);
            return JValue.Null;
        });
        R(PFX + "Camera", "setParallel", "(FFFF)V", (_, a) =>
        {
            N<M3GCamera>(a[0])?.SetParallel(a[1].Float, a[2].Float, a[3].Float, a[4].Float);
            return JValue.Null;
        });
        R(PFX + "Camera", "setGeneric", "(L" + PFX + "Transform;)V", (_, a) =>
        {
            var cam = N<M3GCamera>(a[0]);
            var tf = N<M3GTransform>(a[1]);
            if (cam != null && tf != null) { cam.ProjectionType = M3GCamera.GENERIC; Array.Copy(tf.Matrix, cam.GenericMatrix, 16); }
            return JValue.Null;
        });
        R(PFX + "Camera", "getProjection", "(L" + PFX + "Transform;)I", (_, a) =>
        {
            var cam = N<M3GCamera>(a[0]);
            if (cam == null) return JValue.OfInt(M3GCamera.PERSPECTIVE);
            var tf = N<M3GTransform>(a[1]);
            if (tf != null) Array.Copy(cam.GetProjectionMatrix(), tf.Matrix, 16);
            return JValue.OfInt(cam.ProjectionType);
        });

        // Light
        R(PFX + "Light", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GLight()); return JValue.Null; });
        R(PFX + "Light", "setMode", "(I)V", (_, a) => { var l = N<M3GLight>(a[0]); if (l != null) l.Mode = a[1].Int; return JValue.Null; });
        R(PFX + "Light", "getMode", "()I", (_, a) => JValue.OfInt(N<M3GLight>(a[0])?.Mode ?? M3GLight.DIRECTIONAL));
        R(PFX + "Light", "setColor", "(I)V", (_, a) => { var l = N<M3GLight>(a[0]); if (l != null) l.Color = a[1].Int; return JValue.Null; });
        R(PFX + "Light", "getColor", "()I", (_, a) => JValue.OfInt(N<M3GLight>(a[0])?.Color ?? 0xFFFFFF));
        R(PFX + "Light", "setIntensity", "(F)V", (_, a) => { var l = N<M3GLight>(a[0]); if (l != null) l.Intensity = a[1].Float; return JValue.Null; });
        R(PFX + "Light", "getIntensity", "()F", (_, a) => JValue.OfFloat(N<M3GLight>(a[0])?.Intensity ?? 1f));
        R(PFX + "Light", "setSpotAngle", "(F)V", (_, a) => { var l = N<M3GLight>(a[0]); if (l != null) l.SpotAngle = a[1].Float; return JValue.Null; });
        R(PFX + "Light", "setSpotExponent", "(F)V", (_, a) => { var l = N<M3GLight>(a[0]); if (l != null) l.SpotExponent = a[1].Float; return JValue.Null; });
        R(PFX + "Light", "setAttenuation", "(FFF)V", (_, a) =>
        {
            var l = N<M3GLight>(a[0]);
            if (l != null) { l.AttConst = a[1].Float; l.AttLin = a[2].Float; l.AttQuad = a[3].Float; }
            return JValue.Null;
        });

        // Mesh, VertexBuffer, TriangleStripArray, VertexArray
        R(PFX + "Mesh", "<init>", "(L" + PFX + "VertexBuffer;L" + PFX + "IndexBuffer;L" + PFX + "Appearance;)V", (_, a) =>
        {
            var mesh = new M3GMesh
            {
                VertexBuffer = N<M3GVertexBuffer>(a[1]),
                IndexBuffers = new[] { N<M3GTriangleStripArray>(a[2]) },
                Appearances = new[] { N<M3GAppearance>(a[3]) }
            };
            SetNative(a[0], mesh);
            return JValue.Null;
        });
        R(PFX + "Mesh", "<init>", "(L" + PFX + "VertexBuffer;[L" + PFX + "IndexBuffer;[L" + PFX + "Appearance;)V", (_, a) =>
        {
            var mesh = new M3GMesh { VertexBuffer = N<M3GVertexBuffer>(a[1]) };
            if (a[2].Ref is JavaArray ibArr)
            {
                mesh.IndexBuffers = new M3GTriangleStripArray?[ibArr.Length];
                for (int i = 0; i < ibArr.Length; i++)
                    mesh.IndexBuffers[i] = (ibArr.RefData[i] as JavaObject)?.NativeData as M3GTriangleStripArray;
            }
            if (a[3].Ref is JavaArray apArr)
            {
                mesh.Appearances = new M3GAppearance?[apArr.Length];
                for (int i = 0; i < apArr.Length; i++)
                    mesh.Appearances[i] = (apArr.RefData[i] as JavaObject)?.NativeData as M3GAppearance;
            }
            SetNative(a[0], mesh);
            return JValue.Null;
        });
        R(PFX + "Mesh", "getAppearance", "(I)L" + PFX + "Appearance;", (t, a) =>
        {
            var m = N<M3GMesh>(a[0]);
            var app = m?.GetAppearance(a[1].Int);
            return app != null ? JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "Appearance"), app)) : JValue.Null;
        });
        R(PFX + "Mesh", "setAppearance", "(IL" + PFX + "Appearance;)V", (_, a) =>
        {
            N<M3GMesh>(a[0])?.SetAppearance(a[1].Int, N<M3GAppearance>(a[2]));
            return JValue.Null;
        });
        R(PFX + "Mesh", "getVertexBuffer", "()L" + PFX + "VertexBuffer;", (t, a) =>
        {
            var m = N<M3GMesh>(a[0]);
            return m?.VertexBuffer != null ? JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "VertexBuffer"), m.VertexBuffer)) : JValue.Null;
        });
        R(PFX + "Mesh", "getIndexBuffer", "(I)L" + PFX + "IndexBuffer;", (t, a) =>
        {
            var m = N<M3GMesh>(a[0]);
            int idx = a[1].Int;
            if (m != null && idx >= 0 && idx < m.IndexBuffers.Length && m.IndexBuffers[idx] != null)
                return JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "TriangleStripArray"), m.IndexBuffers[idx]!));
            return JValue.Null;
        });
        R(PFX + "Mesh", "getSubmeshCount", "()I", (_, a) => JValue.OfInt(N<M3GMesh>(a[0])?.IndexBuffers.Length ?? 0));
        R(PFX + "Mesh", "setAlignment", "(L" + PFX + "Node;IL" + PFX + "Node;I)V", (_, _) => JValue.Null);
        R(PFX + "Mesh", "getAnimationTrackCount", "()I", (_, _) => JValue.OfInt(0));

        R(PFX + "VertexBuffer", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GVertexBuffer()); return JValue.Null; });
        R(PFX + "VertexBuffer", "setPositions", "(L" + PFX + "VertexArray;F[F)V", (_, a) =>
        {
            var vb = N<M3GVertexBuffer>(a[0]);
            if (vb != null)
            {
                vb.Positions = N<M3GVertexArray>(a[1]);
                vb.PositionScale = a[2].Float;
                if (a[3].Ref is JavaArray arr && arr.FloatData.Length >= 3)
                    Array.Copy(arr.FloatData, vb.PositionBias, 3);
            }
            return JValue.Null;
        });
        R(PFX + "VertexBuffer", "setNormals", "(L" + PFX + "VertexArray;)V", (_, a) =>
        {
            var vb = N<M3GVertexBuffer>(a[0]);
            if (vb != null) vb.Normals = N<M3GVertexArray>(a[1]);
            return JValue.Null;
        });
        R(PFX + "VertexBuffer", "setColors", "(L" + PFX + "VertexArray;)V", (_, a) =>
        {
            var vb = N<M3GVertexBuffer>(a[0]);
            if (vb != null) vb.Colors = N<M3GVertexArray>(a[1]);
            return JValue.Null;
        });
        R(PFX + "VertexBuffer", "setTexCoords", "(IL" + PFX + "VertexArray;F[F)V", (_, a) =>
        {
            var vb = N<M3GVertexBuffer>(a[0]);
            if (vb == null) return JValue.Null;
            int idx = a[1].Int;
            if (idx >= vb.TexCoords.Length)
            {
                Array.Resize(ref vb.TexCoords, idx + 1);
                Array.Resize(ref vb.TexCoordScale, idx + 1);
                Array.Resize(ref vb.TexCoordBias, idx + 1);
            }
            vb.TexCoords[idx] = N<M3GVertexArray>(a[2]);
            vb.TexCoordScale[idx] = a[3].Float;
            vb.TexCoordBias[idx] = new float[3];
            if (a[4].Ref is JavaArray arr)
                for (int i = 0; i < Math.Min(3, arr.FloatData.Length); i++) vb.TexCoordBias[idx][i] = arr.FloatData[i];
            return JValue.Null;
        });
        R(PFX + "VertexBuffer", "setDefaultColor", "(I)V", (_, a) =>
        {
            var vb = N<M3GVertexBuffer>(a[0]);
            if (vb != null) vb.DefaultColor = a[1].Int;
            return JValue.Null;
        });
        R(PFX + "VertexBuffer", "getVertexCount", "()I", (_, a) =>
            JValue.OfInt(N<M3GVertexBuffer>(a[0])?.Positions?.VertexCount ?? 0));

        R(PFX + "VertexArray", "<init>", "(III)V", (_, a) =>
        {
            var va = new M3GVertexArray
            {
                VertexCount = a[1].Int, ComponentCount = a[2].Int, ComponentSize = a[3].Int,
                Data = new float[a[1].Int * a[2].Int]
            };
            SetNative(a[0], va);
            return JValue.Null;
        });
        R(PFX + "VertexArray", "set", "(I[SI)V", (_, a) =>
        {
            var va = N<M3GVertexArray>(a[0]);
            if (va != null && a[2].Ref is JavaArray arr)
            {
                int first = a[1].Int, count = a[3].Int;
                for (int i = 0; i < count && i < arr.ShortData.Length; i++)
                {
                    int di = (first + i / va.ComponentCount) * va.ComponentCount + (i % va.ComponentCount);
                    if (di < va.Data.Length) va.Data[di] = arr.ShortData[i];
                }
            }
            return JValue.Null;
        });
        R(PFX + "VertexArray", "set", "(II[S)V", (_, a) =>
        {
            var va = N<M3GVertexArray>(a[0]);
            if (va != null && a[3].Ref is JavaArray arr)
            {
                int first = a[1].Int, count = a[2].Int;
                for (int i = 0; i < count * va.ComponentCount && i < arr.ShortData.Length; i++)
                {
                    int di = first * va.ComponentCount + i;
                    if (di < va.Data.Length) va.Data[di] = arr.ShortData[i];
                }
            }
            return JValue.Null;
        });
        R(PFX + "VertexArray", "set", "(I[BI)V", (_, a) =>
        {
            var va = N<M3GVertexArray>(a[0]);
            if (va != null && a[2].Ref is JavaArray arr)
            {
                int first = a[1].Int, count = a[3].Int;
                for (int i = 0; i < count && i < arr.ByteData.Length; i++)
                {
                    int di = (first + i / va.ComponentCount) * va.ComponentCount + (i % va.ComponentCount);
                    if (di < va.Data.Length) va.Data[di] = (sbyte)arr.ByteData[i];
                }
            }
            return JValue.Null;
        });
        R(PFX + "VertexArray", "set", "(II[B)V", (_, a) =>
        {
            var va = N<M3GVertexArray>(a[0]);
            if (va != null && a[3].Ref is JavaArray arr)
            {
                int first = a[1].Int, count = a[2].Int;
                for (int i = 0; i < count * va.ComponentCount && i < arr.ByteData.Length; i++)
                {
                    int di = first * va.ComponentCount + i;
                    if (di < va.Data.Length) va.Data[di] = (sbyte)arr.ByteData[i];
                }
            }
            return JValue.Null;
        });

        R(PFX + "TriangleStripArray", "<init>", "([I[I)V", (_, a) =>
        {
            var tsa = new M3GTriangleStripArray();
            if (a[1].Ref is JavaArray idxArr) tsa.ExplicitIndices = (int[])idxArr.IntData.Clone();
            if (a[2].Ref is JavaArray slArr) tsa.StripLengths = (int[])slArr.IntData.Clone();
            SetNative(a[0], tsa);
            return JValue.Null;
        });
        R(PFX + "TriangleStripArray", "<init>", "(I[I)V", (_, a) =>
        {
            var tsa = new M3GTriangleStripArray { FirstIndex = a[1].Int };
            if (a[2].Ref is JavaArray slArr) tsa.StripLengths = (int[])slArr.IntData.Clone();
            SetNative(a[0], tsa);
            return JValue.Null;
        });

        // Appearance, Material, CompositingMode, PolygonMode, Texture2D, Image2D
        R(PFX + "Appearance", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GAppearance()); return JValue.Null; });
        R(PFX + "Appearance", "setMaterial", "(L" + PFX + "Material;)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]); if (ap != null) ap.Material = N<M3GMaterial>(a[1]);
            return JValue.Null;
        });
        R(PFX + "Appearance", "setCompositingMode", "(L" + PFX + "CompositingMode;)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]); if (ap != null) ap.CompositingMode = N<M3GCompositingMode>(a[1]);
            return JValue.Null;
        });
        R(PFX + "Appearance", "setPolygonMode", "(L" + PFX + "PolygonMode;)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]); if (ap != null) ap.PolygonMode = N<M3GPolygonMode>(a[1]);
            return JValue.Null;
        });
        R(PFX + "Appearance", "setTexture", "(IL" + PFX + "Texture2D;)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]);
            if (ap != null)
            {
                int idx = a[1].Int;
                if (idx >= ap.Textures.Length) Array.Resize(ref ap.Textures, idx + 1);
                ap.Textures[idx] = N<M3GTexture2D>(a[2]);
            }
            return JValue.Null;
        });
        R(PFX + "Appearance", "setFog", "(L" + PFX + "Fog;)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]); if (ap != null) ap.Fog = N<M3GFog>(a[1]);
            return JValue.Null;
        });
        R(PFX + "Appearance", "getFog", "()L" + PFX + "Fog;", (t, a) =>
        {
            var ap = N<M3GAppearance>(a[0]);
            return ap?.Fog != null ? JValue.OfRef(new JavaObject(t.Loader.LoadClass(PFX + "Fog"), ap.Fog)) : JValue.Null;
        });
        R(PFX + "Appearance", "setLayer", "(I)V", (_, a) =>
        {
            var ap = N<M3GAppearance>(a[0]); if (ap != null) ap.Layer = a[1].Int;
            return JValue.Null;
        });

        R(PFX + "Material", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GMaterial()); return JValue.Null; });
        R(PFX + "Material", "setColor", "(II)V", (_, a) =>
        {
            var m = N<M3GMaterial>(a[0]); if (m == null) return JValue.Null;
            int target = a[1].Int, color = a[2].Int;
            if ((target & 1024) != 0) m.AmbientColor = color;
            if ((target & 2048) != 0) m.DiffuseColor = color;
            if ((target & 4096) != 0) m.EmissiveColor = color;
            if ((target & 8192) != 0) m.SpecularColor = color;
            return JValue.Null;
        });
        R(PFX + "Material", "getColor", "(I)I", (_, a) =>
        {
            var m = N<M3GMaterial>(a[0]); if (m == null) return JValue.OfInt(0);
            return JValue.OfInt(a[1].Int switch
            {
                1024 => m.AmbientColor, 2048 => m.DiffuseColor,
                4096 => m.EmissiveColor, 8192 => m.SpecularColor, _ => 0
            });
        });
        R(PFX + "Material", "setShininess", "(F)V", (_, a) => { var m = N<M3GMaterial>(a[0]); if (m != null) m.Shininess = a[1].Float; return JValue.Null; });
        R(PFX + "Material", "setVertexColorTrackingEnable", "(Z)V", (_, a) =>
        {
            var m = N<M3GMaterial>(a[0]); if (m != null) m.VertexColorTracking = a[1].Int != 0;
            return JValue.Null;
        });

        R(PFX + "CompositingMode", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GCompositingMode()); return JValue.Null; });
        R(PFX + "CompositingMode", "setBlending", "(I)V", (_, a) =>
        {
            var cm = N<M3GCompositingMode>(a[0]); if (cm != null) cm.Blending = a[1].Int;
            return JValue.Null;
        });
        R(PFX + "CompositingMode", "setDepthTestEnable", "(Z)V", (_, a) =>
        {
            var cm = N<M3GCompositingMode>(a[0]); if (cm != null) cm.DepthTestEnabled = a[1].Int != 0;
            return JValue.Null;
        });
        R(PFX + "CompositingMode", "setDepthWriteEnable", "(Z)V", (_, a) =>
        {
            var cm = N<M3GCompositingMode>(a[0]); if (cm != null) cm.DepthWriteEnabled = a[1].Int != 0;
            return JValue.Null;
        });
        R(PFX + "CompositingMode", "setAlphaThreshold", "(F)V", (_, a) =>
        {
            var cm = N<M3GCompositingMode>(a[0]); if (cm != null) cm.AlphaThreshold = a[1].Float;
            return JValue.Null;
        });

        R(PFX + "PolygonMode", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GPolygonMode()); return JValue.Null; });
        R(PFX + "PolygonMode", "setCulling", "(I)V", (_, a) => { var pm = N<M3GPolygonMode>(a[0]); if (pm != null) pm.Culling = a[1].Int; return JValue.Null; });
        R(PFX + "PolygonMode", "setShading", "(I)V", (_, a) => { var pm = N<M3GPolygonMode>(a[0]); if (pm != null) pm.Shading = a[1].Int; return JValue.Null; });
        R(PFX + "PolygonMode", "setWinding", "(I)V", (_, a) => { var pm = N<M3GPolygonMode>(a[0]); if (pm != null) pm.Winding = a[1].Int; return JValue.Null; });
        R(PFX + "PolygonMode", "setPerspectiveCorrectionEnable", "(Z)V", (_, a) =>
        {
            var pm = N<M3GPolygonMode>(a[0]); if (pm != null) pm.PerspectiveCorrection = a[1].Int != 0;
            return JValue.Null;
        });
        R(PFX + "PolygonMode", "setTwoSidedLightingEnable", "(Z)V", (_, a) =>
        {
            var pm = N<M3GPolygonMode>(a[0]); if (pm != null) pm.TwoSidedLighting = a[1].Int != 0;
            return JValue.Null;
        });

        R(PFX + "Texture2D", "<init>", "(L" + PFX + "Image2D;)V", (_, a) =>
        {
            var tex = new M3GTexture2D { Image = N<M3GImage2D>(a[1]) };
            SetNative(a[0], tex);
            return JValue.Null;
        });
        R(PFX + "Texture2D", "setBlending", "(I)V", (_, a) => { var t = N<M3GTexture2D>(a[0]); if (t != null) t.Blending = a[1].Int; return JValue.Null; });
        R(PFX + "Texture2D", "setFiltering", "(II)V", (_, a) =>
        {
            var t = N<M3GTexture2D>(a[0]); if (t != null) { t.LevelFilter = a[1].Int; t.ImageFilter = a[2].Int; }
            return JValue.Null;
        });
        R(PFX + "Texture2D", "setWrapping", "(II)V", (_, a) =>
        {
            var t = N<M3GTexture2D>(a[0]); if (t != null) { t.WrapS = a[1].Int; t.WrapT = a[2].Int; }
            return JValue.Null;
        });
        R(PFX + "Texture2D", "setBlendColor", "(I)V", (_, a) => { var t = N<M3GTexture2D>(a[0]); if (t != null) t.BlendColor = a[1].Int; return JValue.Null; });

        R(PFX + "Image2D", "<init>", "(III)V", (_, a) =>
        {
            int format = a[1].Int, w = a[2].Int, h = a[3].Int;
            var img = new M3GImage2D { Format = format, Width = w, Height = h, Pixels = new int[w * h] };
            SetNative(a[0], img);
            return JValue.Null;
        });
        R(PFX + "Image2D", "<init>", "(I[BII)V", (_, a) =>
        {
            int format = a[1].Int, w = a[3].Int, h = a[4].Int;
            var img = new M3GImage2D { Format = format, Width = w, Height = h, Pixels = new int[w * h] };
            if (a[2].Ref is JavaArray arr) DecodeImage2DPixels(img, arr.ByteData);
            SetNative(a[0], img);
            return JValue.Null;
        });
        R(PFX + "Image2D", "<init>", "(III[B)V", (_, a) =>
        {
            int format = a[1].Int, w = a[2].Int, h = a[3].Int;
            var img = new M3GImage2D { Format = format, Width = w, Height = h, Pixels = new int[w * h] };
            if (a[4].Ref is JavaArray arr) DecodeImage2DPixels(img, arr.ByteData);
            SetNative(a[0], img);
            return JValue.Null;
        });
        R(PFX + "Image2D", "<init>", "(ILjava/lang/Object;)V", (_, a) =>
        {
            int format = a[1].Int;
            var midpImg = GetImg(a[2]);
            var img = new M3GImage2D { Format = format };
            if (midpImg != null) { img.Width = midpImg.Width; img.Height = midpImg.Height; img.Pixels = (int[])midpImg.Pixels.Clone(); }
            SetNative(a[0], img);
            return JValue.Null;
        });
        R(PFX + "Image2D", "getWidth", "()I", (_, a) => JValue.OfInt(N<M3GImage2D>(a[0])?.Width ?? 0));
        R(PFX + "Image2D", "getHeight", "()I", (_, a) => JValue.OfInt(N<M3GImage2D>(a[0])?.Height ?? 0));
        R(PFX + "Image2D", "getFormat", "()I", (_, a) => JValue.OfInt(N<M3GImage2D>(a[0])?.Format ?? 0));

        R(PFX + "Background", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GBackground()); return JValue.Null; });
        R(PFX + "Background", "setColor", "(I)V", (_, a) => { var bg = N<M3GBackground>(a[0]); if (bg != null) bg.Color = a[1].Int; return JValue.Null; });
        R(PFX + "Background", "setColorClearEnable", "(Z)V", (_, a) => { var bg = N<M3GBackground>(a[0]); if (bg != null) bg.ColorClearEnabled = a[1].Int != 0; return JValue.Null; });
        R(PFX + "Background", "setDepthClearEnable", "(Z)V", (_, a) => { var bg = N<M3GBackground>(a[0]); if (bg != null) bg.DepthClearEnabled = a[1].Int != 0; return JValue.Null; });
        R(PFX + "Background", "setImage", "(L" + PFX + "Image2D;)V", (_, a) => { var bg = N<M3GBackground>(a[0]); if (bg != null) bg.Image = N<M3GImage2D>(a[1]); return JValue.Null; });
        R(PFX + "Background", "setCrop", "(IIII)V", (_, a) =>
        {
            var bg = N<M3GBackground>(a[0]);
            if (bg != null) { bg.CropX = a[1].Int; bg.CropY = a[2].Int; bg.CropW = a[3].Int; bg.CropH = a[4].Int; }
            return JValue.Null;
        });

        R(PFX + "Fog", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GFog()); return JValue.Null; });
        R(PFX + "Fog", "setColor", "(I)V", (_, a) => { var f = N<M3GFog>(a[0]); if (f != null) f.Color = a[1].Int; return JValue.Null; });
        R(PFX + "Fog", "setMode", "(I)V", (_, a) => { var f = N<M3GFog>(a[0]); if (f != null) f.Mode = a[1].Int; return JValue.Null; });
        R(PFX + "Fog", "setDensity", "(F)V", (_, a) => { var f = N<M3GFog>(a[0]); if (f != null) f.Density = a[1].Float; return JValue.Null; });
        R(PFX + "Fog", "setLinear", "(FF)V", (_, a) => { var f = N<M3GFog>(a[0]); if (f != null) { f.Near = a[1].Float; f.Far = a[2].Float; } return JValue.Null; });

        // Loader
        R(PFX + "Loader", "load", "([BI)[L" + PFX + "Object3D;", (t, a) =>
        {
            if (a[0].Ref is not JavaArray arr) return JValue.Null;
            var loader = new M3GLoader();
            var objects = loader.Load(arr.ByteData);
            var result = t.Loader.CreateRefArray(PFX + "Object3D", objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                string clsName = objects[i] switch
                {
                    M3GWorld => PFX + "World", M3GCamera => PFX + "Camera", M3GLight => PFX + "Light",
                    M3GMesh => PFX + "Mesh", M3GGroup => PFX + "Group",
                    M3GVertexBuffer => PFX + "VertexBuffer", M3GTriangleStripArray => PFX + "TriangleStripArray",
                    M3GAppearance => PFX + "Appearance", M3GMaterial => PFX + "Material",
                    M3GTexture2D => PFX + "Texture2D", M3GImage2D => PFX + "Image2D",
                    M3GBackground => PFX + "Background", M3GCompositingMode => PFX + "CompositingMode",
                    M3GPolygonMode => PFX + "PolygonMode", M3GFog => PFX + "Fog",
                    _ => PFX + "Object3D"
                };
                result.RefData[i] = new JavaObject(t.Loader.LoadClass(clsName), objects[i]);
            }
            return JValue.OfRef(result);
        });
        R(PFX + "Loader", "load", "(Ljava/lang/String;)[L" + PFX + "Object3D;", (t, a) =>
        {
            string? path = Str(a[0]);
            if (path == null) return JValue.Null;
            var data = t.Loader.GetResource(path);
            if (data == null) return JValue.Null;
            var loader = new M3GLoader();
            var objects = loader.Load(data);
            var result = t.Loader.CreateRefArray(PFX + "Object3D", objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                string clsName = objects[i] switch
                {
                    M3GWorld => PFX + "World", M3GCamera => PFX + "Camera", M3GLight => PFX + "Light",
                    M3GMesh => PFX + "Mesh", M3GGroup => PFX + "Group",
                    _ => PFX + "Object3D"
                };
                result.RefData[i] = new JavaObject(t.Loader.LoadClass(clsName), objects[i]);
            }
            return JValue.OfRef(result);
        });

        // AnimationController, AnimationTrack, KeyframeSequence stubs
        R(PFX + "AnimationController", "<init>", "()V", (_, a) => { SetNative(a[0], new M3GAnimationController()); return JValue.Null; });
        R(PFX + "AnimationController", "setActiveInterval", "(II)V", (_, _) => JValue.Null);
        R(PFX + "AnimationController", "setSpeed", "(FI)V", (_, a) => { var c = N<M3GAnimationController>(a[0]); if (c != null) c.Speed = a[1].Float; return JValue.Null; });
        R(PFX + "AnimationController", "setWeight", "(F)V", (_, a) => { var c = N<M3GAnimationController>(a[0]); if (c != null) c.Weight = a[1].Float; return JValue.Null; });
        R(PFX + "AnimationController", "setPosition", "(FI)V", (_, _) => JValue.Null);

        R(PFX + "AnimationTrack", "<init>", "(L" + PFX + "KeyframeSequence;I)V", (_, a) => { SetNative(a[0], new M3GAnimationTrack()); return JValue.Null; });
        R(PFX + "AnimationTrack", "setController", "(L" + PFX + "AnimationController;)V", (_, _) => JValue.Null);

        R(PFX + "KeyframeSequence", "<init>", "(III)V", (_, a) => { SetNative(a[0], new M3GKeyframeSequence()); return JValue.Null; });
        R(PFX + "KeyframeSequence", "setKeyframe", "(I[F)V", (_, _) => JValue.Null);
        R(PFX + "KeyframeSequence", "setDuration", "(I)V", (_, _) => JValue.Null);
        R(PFX + "KeyframeSequence", "setRepeatMode", "(I)V", (_, _) => JValue.Null);
        R(PFX + "KeyframeSequence", "setValidRange", "(II)V", (_, _) => JValue.Null);
    }

    static void RegisterObject3D(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R, string cls)
    {
        R(cls, "setUserID", "(I)V", (_, a) =>
        {
            var obj = N<M3GObject3D>(a[0]); if (obj != null) obj.UserID = a[1].Int;
            return JValue.Null;
        });
        R(cls, "getUserID", "()I", (_, a) => JValue.OfInt(N<M3GObject3D>(a[0])?.UserID ?? 0));
        R(cls, "setUserObject", "(Ljava/lang/Object;)V", (_, a) =>
        {
            var obj = N<M3GObject3D>(a[0]); if (obj != null) obj.UserObject = a[1].Ref;
            return JValue.Null;
        });
        R(cls, "getUserObject", "()Ljava/lang/Object;", (_, a) => JValue.OfRef(N<M3GObject3D>(a[0])?.UserObject));
        R(cls, "addAnimationTrack", "(Ljavax/microedition/m3g/AnimationTrack;)V", (_, _) => JValue.Null);
        R(cls, "animate", "(I)I", (_, _) => JValue.OfInt(0));
        R(cls, "find", "(I)L" + cls + ";", (_, _) => JValue.Null);
        R(cls, "getReferences", "([Ljava/lang/Object;)I", (_, _) => JValue.OfInt(0));
    }

    static void RegisterM3GTransformable<T>(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R, string cls) where T : M3GTransformable
    {
        R(cls, "getTranslation", "([F)V", (_, a) =>
        {
            var t = N<T>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr && arr.FloatData.Length >= 3)
                t.GetTranslation(arr.FloatData);
            return JValue.Null;
        });
        R(cls, "setTranslation", "(FFF)V", (_, a) =>
        {
            N<T>(a[0])?.SetTranslation(a[1].Float, a[2].Float, a[3].Float);
            return JValue.Null;
        });
        R(cls, "getScale", "([F)V", (_, a) =>
        {
            var t = N<T>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr && arr.FloatData.Length >= 3)
                t.GetScale(arr.FloatData);
            return JValue.Null;
        });
        R(cls, "setScale", "(FFF)V", (_, a) =>
        {
            N<T>(a[0])?.SetScale(a[1].Float, a[2].Float, a[3].Float);
            return JValue.Null;
        });
        R(cls, "setOrientation", "(FFFF)V", (_, a) =>
        {
            N<T>(a[0])?.SetOrientation(a[1].Float, a[2].Float, a[3].Float, a[4].Float);
            return JValue.Null;
        });
        R(cls, "getOrientation", "([F)V", (_, a) =>
        {
            var t = N<T>(a[0]);
            if (t != null && a[1].Ref is JavaArray arr && arr.FloatData.Length >= 4)
                t.GetOrientation(arr.FloatData);
            return JValue.Null;
        });
        R(cls, "translate", "(FFF)V", (_, a) =>
        {
            N<T>(a[0])?.Translate(a[1].Float, a[2].Float, a[3].Float);
            return JValue.Null;
        });
        R(cls, "preRotate", "(FFFF)V", (_, a) =>
        {
            N<T>(a[0])?.PreRotate(a[1].Float, a[2].Float, a[3].Float, a[4].Float);
            return JValue.Null;
        });
        R(cls, "postRotate", "(FFFF)V", (_, a) =>
        {
            N<T>(a[0])?.PostRotate(a[1].Float, a[2].Float, a[3].Float, a[4].Float);
            return JValue.Null;
        });
        R(cls, "setTransform", "(Ljavax/microedition/m3g/Transform;)V", (_, a) =>
        {
            var tr = N<T>(a[0]);
            var tf = N<M3GTransform>(a[1]);
            if (tr != null && tf != null) tr.SetTransform(tf);
            return JValue.Null;
        });
        R(cls, "getTransform", "(Ljavax/microedition/m3g/Transform;)V", (_, a) =>
        {
            var tr = N<T>(a[0]);
            var tf = N<M3GTransform>(a[1]);
            if (tr != null && tf != null) tr.GetTransform(tf);
            return JValue.Null;
        });
        R(cls, "getCompositeTransform", "(Ljavax/microedition/m3g/Transform;)V", (_, a) =>
        {
            var tr = N<T>(a[0]);
            var tf = N<M3GTransform>(a[1]);
            if (tr != null && tf != null) Array.Copy(tr.GetCompositeTransform(), tf.Matrix, 16);
            return JValue.Null;
        });
    }

    static void RegisterM3GNode(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R, string cls)
    {
        R(cls, "setRenderingEnable", "(Z)V", (_, a) =>
        {
            var n = N<M3GNode>(a[0]); if (n != null) n.RenderingEnabled = a[1].Int != 0;
            return JValue.Null;
        });
        R(cls, "isRenderingEnabled", "()Z", (_, a) => JValue.OfInt(N<M3GNode>(a[0])?.RenderingEnabled == true ? 1 : 0));
        R(cls, "setPickingEnable", "(Z)V", (_, a) =>
        {
            var n = N<M3GNode>(a[0]); if (n != null) n.PickingEnabled = a[1].Int != 0;
            return JValue.Null;
        });
        R(cls, "isPickingEnabled", "()Z", (_, a) => JValue.OfInt(N<M3GNode>(a[0])?.PickingEnabled == true ? 1 : 0));
        R(cls, "setAlphaFactor", "(F)V", (_, a) =>
        {
            var n = N<M3GNode>(a[0]); if (n != null) n.AlphaFactor = a[1].Float;
            return JValue.Null;
        });
        R(cls, "getAlphaFactor", "()F", (_, a) => JValue.OfFloat(N<M3GNode>(a[0])?.AlphaFactor ?? 1f));
        R(cls, "setScope", "(I)V", (_, a) =>
        {
            var n = N<M3GNode>(a[0]); if (n != null) n.Scope = a[1].Int;
            return JValue.Null;
        });
        R(cls, "getScope", "()I", (_, a) => JValue.OfInt(N<M3GNode>(a[0])?.Scope ?? -1));
        R(cls, "getParent", "()Ljavax/microedition/m3g/Node;", (_, a) =>
            JValue.OfRef(null)); // simplified
    }

    static void RegisterM3GGroup(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R, string cls)
    {
        R(cls, "<init>", "()V", (_, a) => { SetNative(a[0], new M3GGroup()); return JValue.Null; });
        R(cls, "addChild", "(Ljavax/microedition/m3g/Node;)V", (_, a) =>
        {
            var g = N<M3GGroup>(a[0]); var n = N<M3GNode>(a[1]);
            if (g != null && n != null) g.AddChild(n);
            return JValue.Null;
        });
        R(cls, "removeChild", "(Ljavax/microedition/m3g/Node;)V", (_, a) =>
        {
            var g = N<M3GGroup>(a[0]); var n = N<M3GNode>(a[1]);
            if (g != null && n != null) g.RemoveChild(n);
            return JValue.Null;
        });
        R(cls, "getChildCount", "()I", (_, a) => JValue.OfInt(N<M3GGroup>(a[0])?.Children.Count ?? 0));
        R(cls, "getChild", "(I)Ljavax/microedition/m3g/Node;", (t, a) =>
        {
            var g = N<M3GGroup>(a[0]);
            int idx = a[1].Int;
            if (g != null && idx >= 0 && idx < g.Children.Count)
            {
                var child = g.Children[idx];
                string clsName = child switch
                {
                    M3GWorld => "javax/microedition/m3g/World",
                    M3GCamera => "javax/microedition/m3g/Camera",
                    M3GLight => "javax/microedition/m3g/Light",
                    M3GMesh => "javax/microedition/m3g/Mesh",
                    M3GGroup => "javax/microedition/m3g/Group",
                    _ => "javax/microedition/m3g/Node"
                };
                return JValue.OfRef(new JavaObject(t.Loader.LoadClass(clsName), child));
            }
            return JValue.Null;
        });
    }

    static void DecodeImage2DPixels(M3GImage2D img, byte[] data)
    {
        int w = img.Width, h = img.Height;
        int pixCount = w * h;
        switch (img.Format)
        {
            case 99: // RGB
                for (int i = 0; i < pixCount && i * 3 + 2 < data.Length; i++)
                    img.Pixels[i] = unchecked((int)0xFF000000) | (data[i * 3] << 16) | (data[i * 3 + 1] << 8) | data[i * 3 + 2];
                break;
            case 100: // RGBA
                for (int i = 0; i < pixCount && i * 4 + 3 < data.Length; i++)
                    img.Pixels[i] = (data[i * 4 + 3] << 24) | (data[i * 4] << 16) | (data[i * 4 + 1] << 8) | data[i * 4 + 2];
                break;
            case 96: // ALPHA
                for (int i = 0; i < pixCount && i < data.Length; i++)
                    img.Pixels[i] = (data[i] << 24) | 0xFFFFFF;
                break;
            case 97: // LUMINANCE
                for (int i = 0; i < pixCount && i < data.Length; i++)
                    img.Pixels[i] = unchecked((int)0xFF000000) | (data[i] << 16) | (data[i] << 8) | data[i];
                break;
            case 98: // LUMINANCE_ALPHA
                for (int i = 0; i < pixCount && i * 2 + 1 < data.Length; i++)
                    img.Pixels[i] = (data[i * 2 + 1] << 24) | (data[i * 2] << 16) | (data[i * 2] << 8) | data[i * 2];
                break;
        }
    }

    // ── Connector / HttpConnection stubs ──────────────────────────
    static void RegisterConnector(Action<string, string, string, Func<JvmThread, JValue[], JValue>> R)
    {
        R("javax/microedition/io/Connector", "open", "(Ljava/lang/String;)Ljavax/microedition/io/Connection;", (t, a) =>
        {
            var obj = new JavaObject(t.Loader.LoadClass("javax/microedition/io/HttpConnection"));
            obj.NativeData = Str(a[0]);
            return JValue.OfRef(obj);
        });
        R("javax/microedition/io/Connector", "open", "(Ljava/lang/String;I)Ljavax/microedition/io/Connection;", (t, a) =>
        {
            var obj = new JavaObject(t.Loader.LoadClass("javax/microedition/io/HttpConnection"));
            obj.NativeData = Str(a[0]);
            return JValue.OfRef(obj);
        });
        R("javax/microedition/io/Connector", "open", "(Ljava/lang/String;IZ)Ljavax/microedition/io/Connection;", (t, a) =>
        {
            var obj = new JavaObject(t.Loader.LoadClass("javax/microedition/io/HttpConnection"));
            obj.NativeData = Str(a[0]);
            return JValue.OfRef(obj);
        });
        R("javax/microedition/io/Connector", "openInputStream", "(Ljava/lang/String;)Ljava/io/InputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/ByteArrayInputStream"), new MemoryStream(Array.Empty<byte>()))));
        R("javax/microedition/io/Connector", "openOutputStream", "(Ljava/lang/String;)Ljava/io/OutputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/ByteArrayOutputStream"), new MemoryStream())));

        R("javax/microedition/io/HttpConnection", "setRequestMethod", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/io/HttpConnection", "setRequestProperty", "(Ljava/lang/String;Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("javax/microedition/io/HttpConnection", "getResponseCode", "()I", (_, _) => JValue.OfInt(200));
        R("javax/microedition/io/HttpConnection", "getResponseMessage", "()Ljava/lang/String;", (t, _) =>
            JValue.OfRef(t.Loader.CreateString("OK")));
        R("javax/microedition/io/HttpConnection", "getHeaderField", "(Ljava/lang/String;)Ljava/lang/String;", (_, _) => JValue.Null);
        R("javax/microedition/io/HttpConnection", "getLength", "()J", (_, _) => JValue.OfLong(-1));
        R("javax/microedition/io/HttpConnection", "getType", "()Ljava/lang/String;", (t, _) =>
            JValue.OfRef(t.Loader.CreateString("application/octet-stream")));
        R("javax/microedition/io/HttpConnection", "getURL", "()Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((a[0].Ref as JavaObject)?.NativeData as string ?? "")));
        R("javax/microedition/io/HttpConnection", "openInputStream", "()Ljava/io/InputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/ByteArrayInputStream"), new MemoryStream(Array.Empty<byte>()))));
        R("javax/microedition/io/HttpConnection", "openOutputStream", "()Ljava/io/OutputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/ByteArrayOutputStream"), new MemoryStream())));
        R("javax/microedition/io/HttpConnection", "openDataInputStream", "()Ljava/io/DataInputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/DataInputStream"), new MemoryStream(Array.Empty<byte>()))));
        R("javax/microedition/io/HttpConnection", "openDataOutputStream", "()Ljava/io/DataOutputStream;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/io/DataOutputStream"), new MemoryStream())));
        R("javax/microedition/io/HttpConnection", "close", "()V", (_, _) => JValue.Null);
        R("javax/microedition/io/Connection", "close", "()V", (_, _) => JValue.Null);
    }
}

class RecordEnum
{
    public string StoreName;
    public List<int> Ids;
    public int Index;
    public RecordEnum(string name, List<int> ids) { StoreName = name; Ids = ids; }
}
