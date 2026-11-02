namespace Srpg95.Data;

// Event script format: re/FORMAT_NOTES.md §11.2-§11.4.

public enum Op : byte
{
    EndOfScript = 0x00, Message = 0x01, FaceGraphic = 0x02, SwitchOperation = 0x03, ItemManagement = 0x04,
    GoldManagement = 0x05, ScreenDisplay = 0x0b, EraseScreen = 0x0c, PictureDisplay = 0x0d, ErasePicture = 0x0e,
    ScrollScreen = 0x10, FlashTintScreen = 0x12, ShakeScreen = 0x13, ChangeAllyUnit = 0x16, UnitClassChange = 0x17,
    UnitEquipmentChange = 0x18, MoveUnit = 0x1f, ExecuteMovement = 0x20, Bgm = 0x24, SoundEffect = 0x25,
    VariableOperation = 0x29, ConditionalBranch = 0x2a, CharacterChoice = 0x2d, ButtonInput = 0x33, Wait = 0x34,
    SelectMap = 0x36, EndEvent = 0x38, MapComplete = 0x3a, GameOver = 0x3b, ReturnToTitle = 0x3d,
    EndBlock = 0xc7, MessageLine = 0xc9, EndBranch = 0xca, ChoiceCase = 0xcc, EndChoice = 0xcd,
}

/// <summary>One command: u8 opcode, u16 payload length, u8 nesting depth, payload.</summary>
public sealed record Command(Op Op, int Depth, byte[] Payload)
{
    public short I16(int off) => Bin.I16(Payload, off);
    public ushort U16(int off) => Bin.U16(Payload, off);
    public int I32(int off) => Bin.I32(Payload, off);
    public string Str(int off) => Bin.Str(Payload, off, Payload.Length - off);

    /// <summary>Message / message_line: 0 top, 1 centre, 2 bottom.</summary>
    public int Window => U16(0);
    public string Text => Str(2);
    public string[] ChoiceOptions => Bin.Sjis.GetString(Payload, 2, Payload.Length - 2).Split('\0').Take(U16(0)).ToArray();
}

/// <summary>A node of the script tree.</summary>
public abstract record ScriptNode;
public sealed record CommandNode(Command Command) : ScriptNode;
/// <summary>Message (op 01) with its continuation lines (op C9) merged; <paramref name="At"/> is the op 01's command number.</summary>
public sealed record MessageNode(int Window, List<string> Lines, int At = -1) : ScriptNode;
public sealed record BranchNode(Command Condition, List<ScriptNode> Then) : ScriptNode;
/// <param name="At">the character choice's command number</param>
public sealed record ChoiceNode(Command Choice, string[] Options, List<List<ScriptNode>> Cases, int At = -1) : ScriptNode;

public sealed class EventScript
{
    public required string Name { get; init; }
    public required string Comment { get; init; }
    /// <summary>The map file the script is in ("SMAP_001"), set by <see cref="MapData.Load"/>; translations go by it.</summary>
    public string Map { get; set; } = "";
    public required List<Command> Commands { get; init; }
    public required List<ScriptNode> Tree { get; init; }
    /// <summary>Non-null if the file is structurally invalid (no shipped script is).</summary>
    public string? Error { get; init; }
    /// <summary>True if re-encoding Commands reproduces the original stream byte-for-byte.</summary>
    public required bool RoundTrips { get; init; }

    public static EventScript Parse(string name, byte[] file)
    {
        string comment = file.Length >= 100 ? Bin.Str(file, 0, 100) : "";
        if (file.Length < 104)
            return Broken(name, comment, "file shorter than header");
        int n = Bin.I32(file, 100);
        if (n < 0 || n > file.Length - 104)
            return Broken(name, comment, $"bad stream length {n}");
        var s = file.AsSpan(104, n);
        var cmds = new List<Command>();
        int o = 0;
        while (o < s.Length - 1)
        {
            int len = Bin.U16(s, o + 1);
            if (o + 4 + len > s.Length)
                return Broken(name, comment, $"command at {o} overruns stream");
            cmds.Add(new Command((Op)s[o], s[o + 3], s.Slice(o + 4, len).ToArray()));
            o += 4 + len;
        }
        var re = new List<byte>();
        foreach (var c in cmds) { re.Add((byte)c.Op); re.Add((byte)c.Payload.Length); re.Add((byte)(c.Payload.Length >> 8)); re.Add((byte)c.Depth); re.AddRange(c.Payload); }
        re.AddRange(s[o..].ToArray());
        var (tree, problem) = TreeBuilder.Build(cmds);
        return new EventScript
        {
            Name = name, Comment = comment, Commands = cmds, Tree = tree, Error = problem,
            RoundTrips = re.Count == s.Length && s.SequenceEqual(re.ToArray()),
        };
    }

    static EventScript Broken(string name, string comment, string error) => new()
    {
        Name = name, Comment = comment, Commands = new(), Tree = new(), Error = error, RoundTrips = false,
    };
}

/// <summary>
/// Grammar (verified on every intact script):
///   2A(d) body(d+1) C7(d+1) CA(d)
///   2D(d) [CC(d) body(d+1) C7(d+1)]* CD(d)
/// </summary>
public static class TreeBuilder
{
    /// <summary>A message shows at most five lines: its own and the four message lines after it (FUN_0040d18c).</summary>
    public const int MaxMessageLines = 5;

    public static (List<ScriptNode> Tree, string? Problem) Build(List<Command> cmds)
    {
        int pos = 0;
        string? problem = null;
        void Fail(string msg) => problem ??= msg;

        void Expect(Op op, int depth)
        {
            if (pos < cmds.Count && cmds[pos].Op == op && cmds[pos].Depth == depth) pos++;
            else Fail($"expected {op} at depth {depth} at command {pos}");
        }

        List<ScriptNode> Block(int depth)
        {
            var list = new List<ScriptNode>();
            bool afterMessage = false;
            while (pos < cmds.Count)
            {
                var c = cmds[pos];
                if (c.Op is Op.EndBlock or Op.EndBranch or Op.ChoiceCase or Op.EndChoice) return list;
                if (c.Depth != depth) Fail($"command {pos} ({c.Op}) at depth {c.Depth}, expected {depth}");
                int at = pos++;
                bool wasAfterMessage = afterMessage;
                afterMessage = c.Op is Op.Message or Op.MessageLine;
                switch (c.Op)
                {
                    case Op.EndOfScript:
                        break;
                    case Op.Message:
                        list.Add(new MessageNode(c.Window, new List<string> { c.Text }, at));
                        break;
                    case Op.MessageLine:
                        // FUN_0040d18c takes up to four more lines straight after a message (five in all); a line past
                        // those, or one with no message before it, is skipped unseen
                        if (wasAfterMessage && list.Count > 0 && list[^1] is MessageNode m && m.Lines.Count < MaxMessageLines) m.Lines.Add(c.Text);
                        break;
                    case Op.ConditionalBranch:
                        var then = Block(depth + 1);
                        Expect(Op.EndBlock, depth + 1);
                        Expect(Op.EndBranch, depth);
                        list.Add(new BranchNode(c, then));
                        break;
                    case Op.CharacterChoice:
                        var cases = new List<List<ScriptNode>>();
                        while (pos < cmds.Count && cmds[pos].Op == Op.ChoiceCase)
                        {
                            pos++;
                            cases.Add(Block(depth + 1));
                            Expect(Op.EndBlock, depth + 1);
                        }
                        Expect(Op.EndChoice, depth);
                        list.Add(new ChoiceNode(c, c.ChoiceOptions, cases, at));
                        break;
                    default:
                        list.Add(new CommandNode(c));
                        break;
                }
            }
            return list;
        }

        var tree = Block(0);
        if (pos < cmds.Count) Fail($"{cmds.Count - pos} commands left after the top level");
        return (tree, problem);
    }
}
