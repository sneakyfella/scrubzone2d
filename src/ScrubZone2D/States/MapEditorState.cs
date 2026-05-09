#if EDITOR
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameTemplate.GameStates;
using MonoGameTemplate.Input;
using MonoGameTemplate.Rendering;
using ScrubZone2D.Arena;

namespace ScrubZone2D.States;

public sealed class MapEditorState : GameState
{
    // ── Layout constants ────────────────────────────────────────────────────────
    private const int SidebarW  = 310;
    private const int SnapGrid  = 10;
    private const int MaxUndo   = 50;

    // ── Prefab catalogue ────────────────────────────────────────────────────────
    private static readonly (int W, int H, string Label)[] Prefabs =
    [
        (20,  20,  "20x20"),
        (20,  80,  "20x80"),
        (80,  20,  "80x20"),
        (20,  160, "20x160"),
        (160, 20,  "160x20"),
        (100, 100, "100x100"),
        (20,  300, "20x300"),
        (300, 20,  "300x20"),
        (200, 200, "200x200"),
    ];

    private enum Tool      { Place, Select, Spawn }
    private enum SpawnMode { Host, Joiner }
    private enum Field     { None, Name, Width, Height }

    // ── Colors ───────────────────────────────────────────────────────────────────
    private static readonly Color BgSidebar    = new(18, 20, 32);
    private static readonly Color BgViewport   = new( 8,  8, 14);
    private static readonly Color GridColor    = new(30, 35, 45, 180);
    private static readonly Color WallColor    = new(70, 80, 110);
    private static readonly Color WallSelected = new(180, 200, 255);
    private static readonly Color WallGhost    = new(70, 80, 110, 120);
    private static readonly Color BoundaryCol  = new(60, 70, 95);
    private static readonly Color FloorColor   = new(22, 22, 36);
    private static readonly Color SpawnHost    = new(255, 200, 80);
    private static readonly Color SpawnJoiner  = new(80, 200, 255);
    private static readonly Color BtnActive    = new(50, 90, 160);
    private static readonly Color BtnDim       = new(28, 32, 50);
    private static readonly Color BtnDelete    = new(140, 40, 40);
    private static readonly Color BtnSave      = new(45, 110, 55);
    private static readonly Color BtnNew       = new(90, 65, 30);
    private static readonly Color BtnUndo      = new(50, 55, 80);
    private static readonly Color BtnSpawnHost = new(120, 90, 20);
    private static readonly Color BtnSpawnJoin = new(20, 90, 120);

    // ── State ────────────────────────────────────────────────────────────────────
    private readonly GameStateManager _stateManager;

    private MapData  _data;
    private string   _mapPath;

    private Tool      _tool      = Tool.Place;
    private SpawnMode _spawnMode = SpawnMode.Host;
    private int       _prefabIdx = 2;  // default 80×20
    private int       _selectedWall = -1;

    private readonly List<MapData> _undoStack = new();

    // Viewport camera
    private Vector2 _viewOffset;
    private float   _viewZoom = 1f;

    // Mouse drag for pan
    private bool    _panDragging;
    private Point   _panDragStartMouse;
    private Vector2 _panDragStartOffset;
    private int     _prevScrollWheel;

    // Text field input
    private string _nameField;
    private string _widthField;
    private string _heightField;
    private Field  _focused = Field.None;

    private KeyboardState _prevKb;
    private MouseState    _prevMs;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public MapEditorState(GameStateManager stateManager, byte mapIndex = 0xFF)
    {
        _stateManager = stateManager;

        if (mapIndex < MapRegistry.Maps.Count)
        {
            _data    = MapRegistry.Maps[mapIndex].Data.Clone();
            _mapPath = MapRegistry.Maps[mapIndex].Path;
        }
        else
        {
            _data    = new MapData { Name = "NewMap" };
            _mapPath = Path.Combine(AppContext.BaseDirectory, "Maps", "new_map.json");
        }

        _nameField   = _data.Name;
        _widthField  = _data.WorldWidth.ToString();
        _heightField = _data.WorldHeight.ToString();

        // Start viewport centered on map
        _viewOffset = new Vector2(
            (_data.WorldWidth  - (Game1.VirtualWidth  - SidebarW)) / 2f,
            (_data.WorldHeight - Game1.VirtualHeight) / 2f);
    }

    // ── Update ───────────────────────────────────────────────────────────────────

    public override void Update(float dt, StateServices svc)
    {
        var kb = Keyboard.GetState();
        var ms = Mouse.GetState();

        HandleKeyboard(kb);
        HandleMouse(ms);

        _prevKb = kb;
        _prevMs = ms;
    }

    private void HandleKeyboard(KeyboardState kb)
    {
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);

        if (ctrl && InputHelper.IsKeyPressed(Keys.S))  { Save(); return; }
        if (ctrl && InputHelper.IsKeyPressed(Keys.Z))  { Undo(); return; }
        if (InputHelper.IsKeyPressed(Keys.Escape))     { _stateManager.Pop(); return; }
        if (InputHelper.IsKeyPressed(Keys.Delete) && _selectedWall >= 0) DeleteSelected();

        // Text field input
        if (_focused != Field.None)
            HandleTextInput(kb);
    }

    private void HandleTextInput(KeyboardState kb)
    {
        foreach (Keys key in kb.GetPressedKeys())
        {
            if (_prevKb.IsKeyDown(key)) continue;

            if (key == Keys.Tab)   { _focused = (Field)(((int)_focused % 3) + 1); return; }
            if (key == Keys.Enter) { _focused = Field.None; ApplyFields(); return; }

            if (key == Keys.Back)
            {
                switch (_focused)
                {
                    case Field.Name:   if (_nameField.Length   > 0) _nameField   = _nameField[..^1];   break;
                    case Field.Width:  if (_widthField.Length  > 0) _widthField  = _widthField[..^1];  break;
                    case Field.Height: if (_heightField.Length > 0) _heightField = _heightField[..^1]; break;
                }
                continue;
            }

            char? ch = KeyToChar(key, kb);
            if (ch == null) continue;

            switch (_focused)
            {
                case Field.Name:
                    if (_nameField.Length < 32 && (char.IsLetterOrDigit(ch.Value) || ch.Value == '_' || ch.Value == ' '))
                        _nameField += ch;
                    break;
                case Field.Width:
                    if (_widthField.Length < 6 && char.IsDigit(ch.Value))
                        _widthField += ch;
                    break;
                case Field.Height:
                    if (_heightField.Length < 6 && char.IsDigit(ch.Value))
                        _heightField += ch;
                    break;
            }
        }
    }

    private void HandleMouse(MouseState ms)
    {
        var mouseVirt = InputHelper.MousePosition;
        bool inViewport = mouseVirt.X > SidebarW;

        // ── Pan with middle mouse ─────────────────────────────────────────────
        if (ms.MiddleButton == ButtonState.Pressed)
        {
            if (!_panDragging)
            {
                _panDragging        = true;
                _panDragStartMouse  = mouseVirt;
                _panDragStartOffset = _viewOffset;
            }
            else
            {
                var delta = mouseVirt - _panDragStartMouse;
                _viewOffset = _panDragStartOffset - new Vector2(delta.X, delta.Y) / _viewZoom;
            }
        }
        else
        {
            _panDragging = false;
        }

        // ── Zoom with scroll wheel ────────────────────────────────────────────
        int scrollDelta = ms.ScrollWheelValue - _prevScrollWheel;
        if (scrollDelta != 0 && inViewport)
        {
            float zoomFactor = scrollDelta > 0 ? 1.15f : 1f / 1.15f;
            var worldBefore  = ScreenToWorld(mouseVirt);
            _viewZoom        = Math.Clamp(_viewZoom * zoomFactor, 0.1f, 6f);
            var worldAfter   = ScreenToWorld(mouseVirt);
            // Adjust offset so the point under the cursor stays fixed
            _viewOffset += worldBefore - worldAfter;
        }
        _prevScrollWheel = ms.ScrollWheelValue;

        if (!inViewport || _panDragging) return;

        // ── Viewport left-click actions ───────────────────────────────────────
        bool leftJustPressed = ms.LeftButton == ButtonState.Pressed &&
                               _prevMs.LeftButton == ButtonState.Released;
        if (!leftJustPressed) return;

        var worldPos = ScreenToWorld(mouseVirt);
        int wx = Snap((float)worldPos.X);
        int wy = Snap((float)worldPos.Y);

        switch (_tool)
        {
            case Tool.Place:
                PushUndo();
                var (pw, ph, _) = Prefabs[_prefabIdx];
                _data.Walls.Add(new WallDef { Cx = wx, Cy = wy, W = pw, H = ph });
                break;

            case Tool.Select:
                _selectedWall = -1;
                for (int i = _data.Walls.Count - 1; i >= 0; i--)
                {
                    var wd  = _data.Walls[i];
                    var hit = new Rectangle(wd.Cx - wd.W / 2, wd.Cy - wd.H / 2, wd.W, wd.H);
                    if (hit.Contains((int)worldPos.X, (int)worldPos.Y))
                    { _selectedWall = i; break; }
                }
                break;

            case Tool.Spawn:
                PushUndo();
                if (_spawnMode == SpawnMode.Host)
                { _data.SpawnHostX = wx; _data.SpawnHostY = wy; }
                else
                { _data.SpawnJoinerX = wx; _data.SpawnJoinerY = wy; }
                break;
        }
    }

    // ── Undo / helpers ────────────────────────────────────────────────────────────

    private void PushUndo()
    {
        _undoStack.Add(_data.Clone());
        if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _data = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _selectedWall = -1;
        _nameField   = _data.Name;
        _widthField  = _data.WorldWidth.ToString();
        _heightField = _data.WorldHeight.ToString();
    }

    private void DeleteSelected()
    {
        if (_selectedWall < 0 || _selectedWall >= _data.Walls.Count) return;
        PushUndo();
        _data.Walls.RemoveAt(_selectedWall);
        _selectedWall = -1;
    }

    private void ApplyFields()
    {
        _data.Name = string.IsNullOrWhiteSpace(_nameField) ? "NewMap" : _nameField.Trim();
        if (int.TryParse(_widthField,  out int w) && w is > 0 and <= 16000) _data.WorldWidth  = w;
        if (int.TryParse(_heightField, out int h) && h is > 0 and <= 16000) _data.WorldHeight = h;
        _mapPath = Path.Combine(Path.GetDirectoryName(_mapPath)!, $"{SanitizeFilename(_data.Name)}.json");
    }

    private void NewMap()
    {
        PushUndo();
        _data        = new MapData { Name = "NewMap" };
        _mapPath     = Path.Combine(AppContext.BaseDirectory, "Maps", "new_map.json");
        _nameField   = _data.Name;
        _widthField  = _data.WorldWidth.ToString();
        _heightField = _data.WorldHeight.ToString();
        _selectedWall = -1;
    }

    private void Save()
    {
        ApplyFields();
        MapLoader.Save(_data, _mapPath);
        MapRegistry.Reload();
    }

    private static string SanitizeFilename(string name)
    {
        var chars = name.ToLower().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────────

    private Vector2 ScreenToWorld(Point screen) => new(
        (screen.X - SidebarW) / _viewZoom + _viewOffset.X,
        screen.Y              / _viewZoom + _viewOffset.Y);

    private static int Snap(float v) => (int)MathF.Round(v / SnapGrid) * SnapGrid;

    private static char? KeyToChar(Keys key, KeyboardState kb)
    {
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        int k = (int)key;
        if (k >= (int)Keys.A && k <= (int)Keys.Z)
            return shift ? (char)('A' + k - (int)Keys.A) : (char)('a' + k - (int)Keys.A);
        if (k >= (int)Keys.D0 && k <= (int)Keys.D9 && !shift)
            return (char)('0' + k - (int)Keys.D0);
        if (key == Keys.OemMinus && !shift) return '-';
        if (key == Keys.OemPeriod && !shift) return '.';
        if (key == Keys.Space) return ' ';
        return null;
    }

    // ── Draw ──────────────────────────────────────────────────────────────────────

    public override void Draw(SpriteBatch sb, StateServices svc)
    {
        DrawViewport(sb);
        DrawSidebar(sb);
    }

    private void DrawViewport(SpriteBatch sb)
    {
        var worldTransform =
            Matrix.CreateTranslation(-_viewOffset.X, -_viewOffset.Y, 0f) *
            Matrix.CreateScale(_viewZoom) *
            Matrix.CreateTranslation(SidebarW, 0f, 0f);

        sb.Begin(transformMatrix: worldTransform);

        // Background
        UIRenderer.FillRect(sb, new Rectangle(0, 0, _data.WorldWidth, _data.WorldHeight), BgViewport);
        // Floor interior
        const int bw = 20;
        UIRenderer.FillRect(sb, new Rectangle(bw, bw, _data.WorldWidth - bw * 2, _data.WorldHeight - bw * 2), FloorColor);

        // Grid (50-pixel intervals)
        for (int gx = 0; gx <= _data.WorldWidth; gx += 50)
            UIRenderer.FillRect(sb, new Rectangle(gx, 0, 1, _data.WorldHeight), GridColor);
        for (int gy = 0; gy <= _data.WorldHeight; gy += 50)
            UIRenderer.FillRect(sb, new Rectangle(0, gy, _data.WorldWidth, 1), GridColor);

        // Boundary walls
        UIRenderer.FillRect(sb, new Rectangle(0, 0,                        _data.WorldWidth, bw), BoundaryCol);
        UIRenderer.FillRect(sb, new Rectangle(0, _data.WorldHeight - bw,   _data.WorldWidth, bw), BoundaryCol);
        UIRenderer.FillRect(sb, new Rectangle(0, 0,                        bw, _data.WorldHeight), BoundaryCol);
        UIRenderer.FillRect(sb, new Rectangle(_data.WorldWidth - bw, 0,    bw, _data.WorldHeight), BoundaryCol);

        // Interior walls
        for (int i = 0; i < _data.Walls.Count; i++)
        {
            var wd    = _data.Walls[i];
            var color = i == _selectedWall ? WallSelected : WallColor;
            UIRenderer.FillRect(sb, new Rectangle(wd.Cx - wd.W / 2, wd.Cy - wd.H / 2, wd.W, wd.H), color);
        }

        // Spawn markers (16×16 colored squares with a 2px outline)
        DrawSpawnMarker(sb, (int)_data.SpawnHostX,   (int)_data.SpawnHostY,   SpawnHost,   "H");
        DrawSpawnMarker(sb, (int)_data.SpawnJoinerX, (int)_data.SpawnJoinerY, SpawnJoiner, "J");

        // Ghost prefab preview when in Place mode and mouse is in viewport
        var mouse = InputHelper.MousePosition;
        if (_tool == Tool.Place && mouse.X > SidebarW)
        {
            var world = ScreenToWorld(mouse);
            int gx    = Snap((float)world.X);
            int gy    = Snap((float)world.Y);
            var (gpw, gph, _) = Prefabs[_prefabIdx];
            UIRenderer.FillRect(sb, new Rectangle(gx - gpw / 2, gy - gph / 2, gpw, gph), WallGhost);
        }

        sb.End();
    }

    private static void DrawSpawnMarker(SpriteBatch sb, int cx, int cy, Color color, string label)
    {
        const int r = 10;
        UIRenderer.FillRect(sb, new Rectangle(cx - r, cy - r, r * 2, r * 2), color);
        UIRenderer.Text(sb, label, new Vector2(cx - 4, cy - 8), Color.Black);
    }

    private void DrawSidebar(SpriteBatch sb)
    {
        sb.Begin();

        UIRenderer.FillRect(sb, new Rectangle(0, 0, SidebarW, Game1.VirtualHeight), BgSidebar);
        UIRenderer.DrawBorder(sb, new Rectangle(SidebarW - 1, 0, 1, Game1.VirtualHeight), new Color(40, 45, 65), 1);

        int px = 12, py = 10, pw = SidebarW - 24;

        // ── Title ────────────────────────────────────────────────────────────
        UIRenderer.TextCentered(sb, "MAP EDITOR", new Rectangle(0, py, SidebarW, 20),
            new Color(180, 160, 100));
        py += 28;

        // ── Name field ───────────────────────────────────────────────────────
        UIRenderer.Text(sb, "Name:", new Vector2(px, py), Color.Gray, small: true);
        py += 18;
        if (UIRenderer.TextField(sb, _nameField, new Rectangle(px, py, pw, 26), _focused == Field.Name))
            _focused = Field.Name;
        py += 32;

        // ── Map size ─────────────────────────────────────────────────────────
        int hw = (pw - 8) / 2;
        UIRenderer.Text(sb, "Width:", new Vector2(px, py), Color.Gray, small: true);
        UIRenderer.Text(sb, "Height:", new Vector2(px + hw + 8, py), Color.Gray, small: true);
        py += 18;

        if (UIRenderer.TextField(sb, _widthField,  new Rectangle(px, py, hw, 26), _focused == Field.Width))
            _focused = Field.Width;
        if (UIRenderer.TextField(sb, _heightField, new Rectangle(px + hw + 8, py, hw, 26), _focused == Field.Height))
            _focused = Field.Height;
        py += 32;

        if (UIRenderer.Button(sb, "APPLY SIZE", new Rectangle(px, py, pw, 26), BtnDim, Color.LightGray))
            ApplyFields();
        py += 36;

        // ── Tool selection ───────────────────────────────────────────────────
        DrawSeparator(sb, px, py, pw); py += 14;
        UIRenderer.Text(sb, "TOOL", new Vector2(px, py), Color.Gray, small: true);
        py += 18;

        int tw = (pw - 8) / 3;
        if (UIRenderer.Button(sb, "PLACE",  new Rectangle(px,           py, tw, 28), _tool == Tool.Place  ? BtnActive : BtnDim, Color.White))
            { _tool = Tool.Place; _selectedWall = -1; }
        if (UIRenderer.Button(sb, "SELECT", new Rectangle(px + tw + 4,  py, tw, 28), _tool == Tool.Select ? BtnActive : BtnDim, Color.White))
            _tool = Tool.Select;
        if (UIRenderer.Button(sb, "SPAWN",  new Rectangle(px + (tw+4)*2, py, tw, 28), _tool == Tool.Spawn  ? BtnActive : BtnDim, Color.White))
            { _tool = Tool.Spawn; _selectedWall = -1; }
        py += 36;

        // ── Tool-specific panels ──────────────────────────────────────────────
        if (_tool == Tool.Place)
        {
            DrawSeparator(sb, px, py, pw); py += 14;
            UIRenderer.Text(sb, "PREFAB SIZE", new Vector2(px, py), Color.Gray, small: true);
            py += 18;

            int cols = 3;
            int bw2  = (pw - (cols - 1) * 4) / cols;
            for (int i = 0; i < Prefabs.Length; i++)
            {
                int col = i % cols, row = i / cols;
                int bx  = px + col * (bw2 + 4);
                int by  = py + row * 30;
                var col2 = i == _prefabIdx ? BtnActive : BtnDim;
                if (UIRenderer.Button(sb, Prefabs[i].Label, new Rectangle(bx, by, bw2, 24), col2, Color.White))
                    _prefabIdx = i;
            }
            py += ((Prefabs.Length + cols - 1) / cols) * 30 + 6;
        }
        else if (_tool == Tool.Spawn)
        {
            DrawSeparator(sb, px, py, pw); py += 14;
            UIRenderer.Text(sb, "SET SPAWN - click on map", new Vector2(px, py), Color.Gray, small: true);
            py += 18;

            if (UIRenderer.Button(sb, "HOST",   new Rectangle(px,          py, hw, 28), _spawnMode == SpawnMode.Host   ? BtnSpawnHost : BtnDim, Color.White))
                _spawnMode = SpawnMode.Host;
            if (UIRenderer.Button(sb, "JOINER", new Rectangle(px + hw + 8, py, hw, 28), _spawnMode == SpawnMode.Joiner ? BtnSpawnJoin : BtnDim, Color.White))
                _spawnMode = SpawnMode.Joiner;
            py += 36;

            UIRenderer.Text(sb, $"Host:   ({(int)_data.SpawnHostX}, {(int)_data.SpawnHostY})",
                new Vector2(px, py), SpawnHost, small: true);
            py += 18;
            UIRenderer.Text(sb, $"Joiner: ({(int)_data.SpawnJoinerX}, {(int)_data.SpawnJoinerY})",
                new Vector2(px, py), SpawnJoiner, small: true);
            py += 24;
        }
        else if (_tool == Tool.Select && _selectedWall >= 0 && _selectedWall < _data.Walls.Count)
        {
            var wd = _data.Walls[_selectedWall];
            DrawSeparator(sb, px, py, pw); py += 14;
            UIRenderer.Text(sb, "SELECTED WALL", new Vector2(px, py), Color.Gray, small: true);
            py += 18;
            UIRenderer.Text(sb, $"Center: ({wd.Cx}, {wd.Cy})  Size: {wd.W}x{wd.H}",
                new Vector2(px, py), Color.LightGray, small: true);
            py += 22;

            if (UIRenderer.Button(sb, "DELETE  (Del)", new Rectangle(px, py, pw, 28), BtnDelete, Color.White))
                DeleteSelected();
            py += 36;
        }

        // ── Actions ───────────────────────────────────────────────────────────
        // Push actions to near-bottom of sidebar
        int actY = Game1.VirtualHeight - 140;
        DrawSeparator(sb, px, actY, pw); actY += 14;

        if (UIRenderer.Button(sb, "SAVE  (Ctrl+S)", new Rectangle(px, actY, pw, 32), BtnSave, Color.White))
            Save();
        actY += 40;

        if (UIRenderer.Button(sb, "NEW MAP",  new Rectangle(px,          actY, hw, 28), BtnNew,  Color.White))
            NewMap();
        if (UIRenderer.Button(sb, "UNDO (Ctrl+Z)", new Rectangle(px + hw + 8, actY, hw, 28), BtnUndo, Color.LightGray))
            Undo();
        actY += 36;

        if (UIRenderer.Button(sb, "BACK  (Esc)", new Rectangle(px, actY, pw, 28), BtnDim, Color.LightGray))
            _stateManager.Pop();

        // ── Status line ───────────────────────────────────────────────────────
        UIRenderer.Text(sb, $"{_data.Walls.Count} walls | zoom {_viewZoom:F2}x",
            new Vector2(px, Game1.VirtualHeight - 16), new Color(60, 70, 90), small: true);

        sb.End();
    }

    private static void DrawSeparator(SpriteBatch sb, int px, int py, int pw)
        => UIRenderer.DrawBorder(sb, new Rectangle(px, py, pw, 1), new Color(40, 45, 65), 1);
}
#endif
