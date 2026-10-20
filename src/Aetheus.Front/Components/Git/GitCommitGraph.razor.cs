// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Git;

/// <summary>
/// O: renders a commit history as an SVG lane graph (replacing the ASCII <c>git log --graph</c> dump).
/// Columns are assigned with a standard lane-packing walk over the commit list (newest first); each
/// commit's parents continue its lane, merges/branches open or close lanes. Edges are drawn child→parent
/// and stay in the child's column until bending into the parent near its row.
/// </summary>
public partial class GitCommitGraph
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public List<GitLightCommitDto> Commits { get; set; } = [];

    /// <summary>The repository the commits belong to, so each SHA links to its commit page. Without
    /// it the SHA is plain text.</summary>
    [Parameter] public int? RepoId { get; set; }

    private string? CommitHref(GitLightCommitDto commit) => RepoId is { } repoId
        ? $"/git-repositories/{repoId}/commits/{Uri.EscapeDataString(commit.Sha)}"
        : null;

    // Geometry (px). RowHeight must match the .git-svg-row CSS height so SVG nodes align with the text rows.
    private const int RowHeight = 34;
    private const int ColWidth = 18;
    private const int NodeRadius = 5;
    private const int LeftPad = 12;

    private static readonly string[] LaneColors =
    [
        "#7c4dff", "#26a69a", "#ef5350", "#ffa726", "#42a5f5",
        "#ec407a", "#9ccc65", "#ab47bc", "#26c6da", "#ff7043"
    ];

    private sealed record Node(double X, double Y, int Col, GitLightCommitDto Commit);
    private sealed record Edge(string Path, string Color);

    private List<Node> _nodes = [];
    private List<Edge> _edges = [];
    private int _maxCol;

    private double SvgWidth => LeftPad * 2 + (_maxCol + 1) * ColWidth;
    private double SvgHeight => Commits.Count * RowHeight;

    protected override void OnParametersSet() => Build();

    private void Build()
    {
        _nodes = [];
        _edges = [];
        _maxCol = 0;

        var colOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var edgeLaneOf = new Dictionary<(string Child, string Parent), int>();
        var lanes = new List<string?>(); // SHA each lane is currently waiting to place next

        int FreeOrAppend()
        {
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is null) return i;
            lanes.Add(null);
            return lanes.Count - 1;
        }

        for (var row = 0; row < Commits.Count; row++)
        {
            var c = Commits[row];

            var mine = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] == c.Sha) mine.Add(i);

            var col = mine.Count > 0 ? mine[0] : FreeOrAppend();
            colOf[c.Sha] = col;
            rowOf[c.Sha] = row;

            // Children converging here free their extra lanes.
            for (var k = 1; k < mine.Count; k++) lanes[mine[k]] = null;

            if (c.ParentShas.Count > 0)
            {
                lanes[col] = c.ParentShas[0];
                edgeLaneOf[(c.Sha, c.ParentShas[0])] = col;
                for (var k = 1; k < c.ParentShas.Count; k++)
                {
                    var existing = lanes.IndexOf(c.ParentShas[k]);
                    var lane = existing >= 0 ? existing : FreeOrAppend();
                    lanes[lane] = c.ParentShas[k];
                    edgeLaneOf[(c.Sha, c.ParentShas[k])] = lane;
                }
            }
            else
            {
                lanes[col] = null;
            }

            _maxCol = Math.Max(_maxCol, Math.Max(col, lanes.Count - 1));
        }

        foreach (var c in Commits)
            _nodes.Add(new Node(X(colOf[c.Sha]), Y(rowOf[c.Sha]), colOf[c.Sha], c));

        foreach (var c in Commits)
        {
            var cCol = colOf[c.Sha];
            var cRow = rowOf[c.Sha];
            foreach (var parent in c.ParentShas)
            {
                if (!rowOf.TryGetValue(parent, out var pRow)) continue; // parent outside the fetched window
                _edges.Add(new Edge(EdgePath(cCol, cRow, colOf[parent], pRow, edgeLaneOf[(c.Sha, parent)]), LaneColor(cCol)));
            }
        }
    }

    private static double X(int col) => LeftPad + col * ColWidth;
    private static double Y(int row) => row * RowHeight + RowHeight / 2.0;

    private static string EdgePath(int childCol, int childRow, int parentCol, int parentRow, int laneCol)
    {
        double x1 = X(childCol), y1 = Y(childRow), x2 = X(parentCol), y2 = Y(parentRow), laneX = X(laneCol);
        if (childCol == parentCol && childCol == laneCol)
            return Fmt($"M {x1} {y1} L {x2} {y2}");

        var startBend = y1 + RowHeight / 2.0;
        var endBend = Math.Max(startBend, y2 - RowHeight / 2.0);
        return Fmt($"M {x1} {y1} C {x1} {startBend} {laneX} {startBend} {laneX} {startBend} L {laneX} {endBend} C {laneX} {endBend} {x2} {y2} {x2} {y2}");
    }

    private static string LaneColor(int col) => LaneColors[((col % LaneColors.Length) + LaneColors.Length) % LaneColors.Length];

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static string CommitTitle(string message)
    {
        var nl = message.IndexOf('\n');
        return nl < 0 ? message : message[..nl];
    }
}
