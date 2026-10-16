using DalamudMCP.Tools;

var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
ScreenNode Fill(float width) => new(1043, width, 1, 0, 0, true);
var marker = new ScreenNode(1246, 480, 0.075f, 0, 0, true);
Check(NodeScreenMatch.Matches(Fill(223), marker, 0.5), "observed BasketBall success: native screen center");
Check(!NodeScreenMatch.Matches(Fill(203), marker, 0.5), "local-coordinate false match is rejected");
Check(!NodeScreenMatch.Matches(Fill(17), marker, 0.5), "observed miss is rejected");
Check(NodeScreenMatch.Matches(new(1564.5f, 223, 1.5f, 0, 0, true), new(1869, 480, 0.1125f, 0, 0, true), 0.5), "matching is preserved at 150 percent UI scale");
Check(NodeScreenMatch.Matches(Fill(223), marker with { X = 1246 + 1 }, 0.5), "small marker movement uses current position");
Check(!NodeScreenMatch.Matches(Fill(223), marker with { X = 1300 }, 0.5), "moved marker invalidates old aim");
Check(!NodeScreenMatch.Matches(Fill(223) with { Visible = false }, marker, 0.5), "hidden observed node cannot trigger input");
Check(!NodeScreenMatch.Matches(Fill(223), marker with { Visible = false }, 0.5), "hidden marker cannot trigger input");
Check(!NodeScreenMatch.Matches(Fill(223), marker with { M12 = 1 }, 0.5), "rotated geometry is refused");
Check(!NodeScreenMatch.Matches(Fill(223), marker with { M11 = -1 }, 0.5), "reflected geometry is refused");
Check(!NodeScreenMatch.Matches(Fill(223), marker with { X = float.NaN }, 0.5), "non-finite geometry is refused");
Check(!NodeScreenMatch.Matches(Fill(223), marker, 0), "empty target window is refused");
Console.WriteLine($"{count}/{count} passed");
