
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppTraceProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TRACE_PROBE") == "1";

        private const int MaxDepth = 5;

        private sealed class Node
        {
            public string Fmt = "?";
            public int Width;
            public int Height;
            public readonly HashSet<ulong> Inputs = new();
        }

        private static readonly Dictionary<ulong, Node> _graph = new();
        private static long _lastReportMs;
        private static int _announced;

        public static void AnnounceOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0)
            {
                return;
            }

            string v = Environment.GetEnvironmentVariable("RYUJINX_TRACE_PROBE");
            Logger.Info?.Print(LogClass.Gpu,
                $"TRACEPROBE gate check: RYUJINX_TRACE_PROBE={(v ?? "<unset>")} -> enabled={Enabled}. " +
                (Enabled ? "Probe ACTIVE." : "Probe OFF."));
        }

        private static ulong Va(Texture t)
        {
            try
            {
                return t.Range.GetSubRange(0).Address;
            }
            catch
            {
                return 0;
            }
        }

        private static Node GetOrAdd(ulong va, TextureInfo info)
        {
            if (!_graph.TryGetValue(va, out Node n))
            {
                n = new Node();
                _graph[va] = n;
            }

            n.Fmt = info.FormatInfo.Format.ToString();
            n.Width = info.Width;
            n.Height = info.Height;
            return n;
        }

        public static void OnEdge(Texture renderTarget, Texture input)
        {
            if (renderTarget == null || input == null)
            {
                return;
            }

            ulong rtVa = Va(renderTarget);
            ulong inVa = Va(input);

            if (rtVa == inVa || inVa == 0)
            {
                return;
            }

            lock (_graph)
            {
                Node rt = GetOrAdd(rtVa, renderTarget.Info);
                GetOrAdd(inVa, input.Info);
                rt.Inputs.Add(inVa);
            }
        }

        public static void OnFrameBoundary()
        {
            long now = Environment.TickCount64;

            lock (_graph)
            {
                if (now - _lastReportMs < 3000)
                {
                    return;
                }
                _lastReportMs = now;

                Logger.Info?.Print(LogClass.Gpu,
                    $"TRACEPROBE dependency walk from DoF inputs (nodes={_graph.Count}) [H]=CaseA history =====");

                foreach (ulong root in MvppDofProbe.DofInputVas)
                {
                    if (!_graph.TryGetValue(root, out Node rn))
                    {
                        continue;
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"TRACEPROBE  DoF-input 0x{root:X} {rn.Fmt}/{rn.Width}x{rn.Height}");

                    var visited = new HashSet<ulong>();
                    Walk(root, 1, visited);
                }
            }
        }

        private static void Walk(ulong va, int depth, HashSet<ulong> visited)
        {
            if (depth > MaxDepth || !visited.Add(va))
            {
                return;
            }

            if (!_graph.TryGetValue(va, out Node n) || n.Inputs.Count == 0)
            {
                return;
            }

            foreach (ulong inVa in n.Inputs)
            {
                _graph.TryGetValue(inVa, out Node inNode);
                (long caseA, long caseB) = MvppFeedbackProbe.Classify(inVa);
                bool history = caseA > 0 && caseA >= caseB;
                bool dofIn = MvppDofProbe.DofInputVas.Contains(inVa);

                var sb = new StringBuilder();
                sb.Append("TRACEPROBE  ");
                sb.Append(' ', depth * 2);
                sb.Append($"<- 0x{inVa:X} {(inNode != null ? $"{inNode.Fmt}/{inNode.Width}x{inNode.Height}" : "?")}");
                sb.Append($" [A={caseA},B={caseB}]");
                if (history)
                {
                    sb.Append(" [H]");
                }
                if (dofIn)
                {
                    sb.Append(" [DoF-in]");
                }

                Logger.Info?.Print(LogClass.Gpu, sb.ToString());

                Walk(inVa, depth + 1, visited);
            }
        }
    }
}
