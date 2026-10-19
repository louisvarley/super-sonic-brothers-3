namespace SonicBros3.Logic;

/// <summary>
/// ObjectsManager (ROM 0x1B824, 1-player path): spawns objects from the act's placement list as the camera moves
/// in 128 px steps, keeping right/left list pointers ($F770/$F774) and the respawn table ($FC00: right/left
/// counters, then one byte per "remember state" object whose bit 7 means "loaded or destroyed").
/// The placement list itself lives in the extracted objects.json; this class keeps it as a 6-byte-per-record
/// image so the pointer arithmetic matches the original.
/// </summary>
public sealed class ObjectManager
{
    private readonly Ram _ram;
    private readonly byte[] _list;            // {x, y|flags, id, subtype} records, with sentinels around them
    private const int ListBase = 6;           // index of the first record (a leading x=0 boundary record)

    private readonly int _pointerBase;

    /// <param name="listAddress">The list's address in the original; RAM pointers are kept in that numbering.</param>
    /// <param name="boundaryX">x of the record before the list.</param>
    public ObjectManager(Ram ram, IReadOnlyList<S2Port.Extraction.Levels.ObjectPlacement> placements, int listAddress = 0, int boundaryX = 0)
    {
        _ram = ram;
        _pointerBase = listAddress == 0 ? ListBase : listAddress;
        // Leading boundary record (x = 0) and trailing x = $FFFF terminator, as the ROM lists have.
        _list = new byte[6 + placements.Count * 6 + 6];
        int i = ListBase;
        foreach (var p in placements)
        {
            int y = p.Y | (p.FlipX ? 0x2000 : 0) | (p.FlipY ? 0x4000 : 0) | (p.Remember ? 0x8000 : 0);
            W16(i, p.X); W16(i + 2, y); _list[i + 4] = (byte)p.Id; _list[i + 5] = (byte)p.Subtype;
            i += 6;
        }
        W16(i, 0xFFFF);
        if (listAddress != 0) W16(0, boundaryX);
    }

    private int X(int rec) => (_list[rec] << 8) | _list[rec + 1];
    private bool Remember(int rec) => (_list[rec + 2] & 0x80) != 0;
    private void W16(int i, int v) { _list[i] = (byte)(v >> 8); _list[i + 1] = (byte)v; }
    private int Ptr(int addr) => (int)_ram.U32(addr) - _pointerBase + ListBase;
    private void SetPtr(int addr, int v) => _ram.W32(addr, v - ListBase + _pointerBase);

    public void Run()
    {
        if (_ram.U16(Vars.TwoPlayerMode) != 0) throw new UnportedException(0x1B8FC, "ObjectsManager 2P");
        if (_ram.U8(Vars.ObjPlacementRoutine) == 0) Init();
        Main();
    }

    /// <summary>ObjectsManager_Init (0x1B83C).</summary>
    private void Init()
    {
        _ram.W8(Vars.ObjPlacementRoutine, _ram.U8(Vars.ObjPlacementRoutine) + 2);
        foreach (int a in new[] { Vars.ObjLoadAddrRight, Vars.ObjLoadAddrLeft, Vars.ObjLoadAddrRightP2, Vars.ObjLoadAddrLeftP2 }) SetPtr(a, ListBase);
        _ram.W16(Vars.ObjectRespawnTable, 0x0101);
        for (int i = 2; i < 0x180; i++) _ram.W8(Vars.ObjectRespawnTable + i, 0);
        int d6 = _ram.U16(Vars.CameraXPos) - 0x80;
        if (d6 < 0) d6 = 0;
        d6 &= 0xFF80;
        int a0 = Ptr(Vars.ObjLoadAddrRight);
        while (d6 > X(a0))
        {
            if (Remember(a0)) _ram.W8(Vars.ObjectRespawnTable, _ram.U8(Vars.ObjectRespawnTable) + 1);
            a0 += 6;
        }
        SetPtr(Vars.ObjLoadAddrRight, a0);
        SetPtr(Vars.ObjLoadAddrRightP2, a0);
        a0 = Ptr(Vars.ObjLoadAddrLeft);
        d6 -= 0x80;
        if (d6 >= 0)
        {
            while (d6 > X(a0))
            {
                if (Remember(a0)) _ram.W8(Vars.ObjectRespawnTable + 1, _ram.U8(Vars.ObjectRespawnTable + 1) + 1);
                a0 += 6;
            }
        }
        SetPtr(Vars.ObjLoadAddrLeft, a0);
        SetPtr(Vars.ObjLoadAddrLeftP2, a0);
        _ram.W16(Vars.CameraXPosLast, 0xFFFF);
        _ram.W16(Vars.CameraXPosLastP2, 0xFFFF);
    }

    /// <summary>ObjectsManager_Main (0x1B904).</summary>
    private void Main()
    {
        _ram.W16(Vars.CameraXPosCoarse, (_ram.U16(Vars.CameraXPos) - 0x80) & 0xFF80);
        int d6 = _ram.U16(Vars.CameraXPos) & 0xFF80;
        int last = (short)_ram.U16(Vars.CameraXPosLast);
        if (d6 == (last & 0xFFFF)) return;
        int d2 = 0;
        if ((short)d6 >= last)
        {
            // Moving right (0x1B98A): load up to camera + $280, drop behind camera - $80.
            _ram.W16(Vars.CameraXPosLast, d6);
            int a0 = Ptr(Vars.ObjLoadAddrRight);
            int limit = d6 + 0x280;
            while (limit > X(a0))
            {
                if (Remember(a0)) { d2 = _ram.U8(Vars.ObjectRespawnTable); _ram.W8(Vars.ObjectRespawnTable, d2 + 1); }
                if (!ChkLoadObj(ref a0, d2)) break;
            }
            SetPtr(Vars.ObjLoadAddrRight, a0);
            a0 = Ptr(Vars.ObjLoadAddrLeft);
            int back = d6 + 0x280 - 0x300;
            if (back >= 0)
            {
                while (back > X(a0))
                {
                    if (Remember(a0)) _ram.W8(Vars.ObjectRespawnTable + 1, _ram.U8(Vars.ObjectRespawnTable + 1) + 1);
                    a0 += 6;
                }
            }
            SetPtr(Vars.ObjLoadAddrLeft, a0);
            return;
        }

        // Moving left (0x1B92C): load down to camera - $80, drop beyond camera + $280.
        _ram.W16(Vars.CameraXPosLast, d6);
        {
            int a0 = Ptr(Vars.ObjLoadAddrLeft);
            int limit = d6 - 0x80;
            if (limit >= 0)
            {
                while (limit < (short)X(a0 - 6))   // signed (cmp.w / bge): the record before the list stops the walk
                {
                    a0 -= 6;
                    if (Remember(a0)) { _ram.W8(Vars.ObjectRespawnTable + 1, _ram.U8(Vars.ObjectRespawnTable + 1) - 1); d2 = _ram.U8(Vars.ObjectRespawnTable + 1); }
                    int at = a0;
                    if (!ChkLoadObj(ref at, d2))
                    {
                        if (Remember(a0)) _ram.W8(Vars.ObjectRespawnTable + 1, _ram.U8(Vars.ObjectRespawnTable + 1) + 1);
                        a0 += 6;
                        break;
                    }
                }
            }
            SetPtr(Vars.ObjLoadAddrLeft, a0);
            a0 = Ptr(Vars.ObjLoadAddrRight);
            int far = limit + 0x300;
            while ((short)far <= (short)X(a0 - 6))   // signed (cmp.w / bgt)
            {
                if (Remember(a0 - 6)) _ram.W8(Vars.ObjectRespawnTable, _ram.U8(Vars.ObjectRespawnTable) - 1);
                a0 -= 6;
            }
            SetPtr(Vars.ObjLoadAddrRight, a0);
        }
    }

    /// <summary>ChkLoadObj (0x1BCB6). Returns false only when no object slot is free (record not consumed).</summary>
    private bool ChkLoadObj(ref int a0, int d2)
    {
        if (Remember(a0))
        {
            if (_ram.TestSet(Vars.ObjectRespawnTable + 2 + d2, 7)) { a0 += 6; return true; }   // destroyed earlier: skip
        }
        int slot = Objects.FindFreeSlot(_ram);
        if (slot < 0) return false;
        int y = (_list[a0 + 2] << 8) | _list[a0 + 3];
        _ram.W16(slot + 0x08, X(a0));
        if ((y & 0x8000) != 0) _ram.W8(slot + 0x23, d2);
        _ram.W16(slot + 0x0C, y & 0xFFF);
        int flags = ((y << 3) | (y >> 13)) & 3;
        _ram.W8(slot + 0x01, flags);
        _ram.W8(slot + 0x22, flags);
        _ram.W8(slot, _list[a0 + 4]);
        _ram.W8(slot + 0x28, _list[a0 + 5]);
        a0 += 6;
        return true;
    }
}
