using Amethyst;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}

static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', bool ctrl = false) =>
    new(ch, key, false, false, ctrl);

static ConsoleKeyInfo Letter(char ch) => Key(ConsoleKey.NoName, ch);

static void Type(ConsoleLineEditor editor, string value,
    Func<string, IReadOnlyList<string>> complete)
{
    foreach (char ch in value) editor.Handle(Letter(ch), complete);
}

string[] users = ["ExamplePlayer", "Alex", "Bob"];
IReadOnlyList<string> Complete(string prefix) =>
    ConsoleCommandCompletion.Suggest(prefix, users, ["ExamplePlayer"]);

var editor = new ConsoleLineEditor(3);
Type(editor, "op Fred", Complete);
editor.Handle(Key(ConsoleKey.Tab), Complete);
Check(editor.Text == "op ExamplePlayer ", "Tab completes connected player names");
Type(editor, "1", Complete);
Check(editor.Handle(Key(ConsoleKey.Enter), Complete).Submitted == "op ExamplePlayer 1",
    "Enter submits the command text");
Check(editor.Text == "", "Enter clears the command input");
Type(editor, "ki", Complete);
editor.Handle(Key(ConsoleKey.Tab), Complete);
Check(editor.Text == "kick ", "Tab completes command names");
editor.Handle(Key(ConsoleKey.Enter), Complete);
Type(editor, "draft", Complete);
editor.Handle(Key(ConsoleKey.UpArrow), Complete);
Check(editor.Text == "kick", "Up recalls recent commands");
editor.Handle(Key(ConsoleKey.UpArrow), Complete);
Check(editor.Text == "op ExamplePlayer 1", "History walks backward");
editor.Handle(Key(ConsoleKey.DownArrow), Complete);
editor.Handle(Key(ConsoleKey.DownArrow), Complete);
Check(editor.Text == "draft", "Down restores unfinished input");
editor.Handle(Key(ConsoleKey.Home), Complete);
Type(editor, "x", Complete);
Check(editor.Text == "xdraft" && editor.Cursor == 1,
    "Home, mid-line insert and cursor positioning");
editor.Handle(Key(ConsoleKey.Delete), Complete);
Check(editor.Text == "xraft", "Delete removes character under cursor");
editor.Handle(Key(ConsoleKey.End), Complete);
editor.Handle(Key(ConsoleKey.Backspace), Complete);
Check(editor.Text == "xraf", "Backspace removes previous character");
editor.Handle(Key(ConsoleKey.U, ctrl: true), Complete);
Check(editor.Text == "" && editor.Cursor == 0, "Ctrl+U erases command prefix");

// Log capture works in a redirected/non-interactive environment and has no
// ANSI control codes. Verify that concurrent producers do not overwrite data.
TextWriter oldOut = Console.Out;
TextWriter oldError = Console.Error;
var capturedOut = new StringWriter();
var capturedError = new StringWriter();
Console.SetOut(capturedOut);
Console.SetError(capturedError);
try
{
    await using (var terminal = new ServerConsole("plain", 5))
    {
        Console.WriteLine("[auth] ExamplePlayer authenticated");
        Console.WriteLine("[chat] hi");
        Console.Error.WriteLine("[ERROR] something failed");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            Console.WriteLine($"event {i}"))));
    }
}
finally
{
    Console.SetOut(oldOut);
    Console.SetError(oldError);
}
Check(capturedOut.ToString().Contains("[AUTH] [auth] ExamplePlayer authenticated"),
    "Auth messages are categorized");
Check(capturedOut.ToString().Contains("[CHAT] [chat] hi"),
    "Chat messages are categorized");
Check(capturedError.ToString().Contains("[ERROR] [ERROR] something failed"),
    "Errors are sent to stderr in plain mode");
Check(Enumerable.Range(0, 100).All(i =>
    capturedOut.ToString().Contains($"[INFO] event {i}" + Environment.NewLine)),
    "Concurrent log lines are serialized without loss");
Check(!capturedOut.ToString().Contains("\u001b["),
    "Plain console output does not contain terminal escape codes");
Check(ServerConsole.Classify("[chunks] sent batch") == ServerLogLevel.Debug &&
    ServerConsole.Classify("WARNING: not secure") == ServerLogLevel.Warning,
    "Log classification handles debug and warning entries");

Console.WriteLine("PASS: interactive console editor, completions and logging tests");
