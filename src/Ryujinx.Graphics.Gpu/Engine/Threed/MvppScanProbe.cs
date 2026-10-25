
using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppScanProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SCAN") == "1";

        private const int PassWindowMs = 5000;
        private const int DeepScanIntervalMs = 1000;
        private const int DeepScanFallbackMs = 500;
        private const int MaxBytesPerBuffer = 4096;
        private const int MaxDetailLines = 28;

        private const int MaxBuffers = 48;
        private const int MaxCandidates = 512;
        private const int MaxProjPaired = 6;
        private const int MaxViewPaired = 10;
        private const int MaxProducts = MaxProjPaired * MaxViewPaired * 2;

        private struct DepthForm
        {
            public int Width;
            public int Height;
            public Ryujinx.Graphics.GAL.Format Format;
            public bool Scaled;
            public int Count;
        }

        private static int _liveMainW;
        private static int _liveMainH;
        private static long _liveMainArea;
        private static float _mainAspect;

        private static long _passWindowStartMs;
        private static int _draws;
        private static int _drawsWithDepth;
        private static int _drawsNonSquareDepth;
        private static readonly DepthForm[] _depthForms = new DepthForm[8];
        private static int _depthFormCount;
        private static int _depthFormOverflow;

        private static readonly DepthForm[] _colorForms = new DepthForm[12];
        private static int _colorFormCount;
        private static int _colorFormOverflow;

        private static long _lastDeepScanMs;
        private static bool _lastScanOnMainPass;
        private static bool _wantDeepScan;
        private static long _wantDeepScanSinceMs;
        private static ulong _lastCbufLayoutFp;
        private static ulong _lastFoundSetFp;
        private static int _deepScans;

        private static int _lastProjCount;
        private static int _lastViewCount;
        private static int _lastViewKept;
        private static int _lastVpProductCount;
        private static int _lastCanonicalCount;
        private static int _lastStandaloneVp;
        private static int _lastStandaloneSquare;

        public static void OnDraw(GpuChannel channel)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPPSCAN: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            long now = Environment.TickCount64;

            _draws++;

            Image.Texture ds = channel.TextureManager.RenderTargetDepthStencil;

            if (ds != null)
            {
                _drawsWithDepth++;

                int w = ds.Info.Width;
                int h = ds.Info.Height;

                if (w != h)
                {
                    _drawsNonSquareDepth++;

                    long area = (long)w * h;

                    if (area > _liveMainArea)
                    {
                        _liveMainArea = area;
                        _liveMainW = w;
                        _liveMainH = h;

                        if (_mainAspect <= 0f)
                        {
                            _mainAspect = (float)w / h;
                        }
                    }
                }

                RecordDepthForm(w, h, ds.Info.FormatInfo.Format, channel.TextureManager.RenderTargetScale > 1f);
            }

            Image.Texture c0 = channel.TextureManager.RenderTargetColor0;

            if (c0 != null)
            {
                RecordColorForm(c0.Info.Width, c0.Info.Height, c0.Info.FormatInfo.Format);
            }

            if (now - _passWindowStartMs >= PassWindowMs)
            {
                if (_passWindowStartMs != 0)
                {
                    FlushPassWindow();
                }

                _passWindowStartMs = now;
            }

            if (!_wantDeepScan && now - _lastDeepScanMs >= DeepScanIntervalMs)
            {
                _wantDeepScan = true;
                _wantDeepScanSinceMs = now;
            }

            bool onMainPass = ds != null && ds.Info.Width != ds.Info.Height &&
                (long)ds.Info.Width * ds.Info.Height >= _liveMainArea;

            if (_wantDeepScan &&
                (onMainPass ||
                 (ds != null && now - _wantDeepScanSinceMs >= DeepScanFallbackMs) ||
                 now - _wantDeepScanSinceMs >= DeepScanFallbackMs * 2))
            {
                _wantDeepScan = false;
                _lastDeepScanMs = now;
                _lastScanOnMainPass = onMainPass;
                DeepScan(channel, ds);
            }
        }

        private static void RecordDepthForm(int width, int height, Ryujinx.Graphics.GAL.Format format, bool scaled)
        {
            for (int i = 0; i < _depthFormCount; i++)
            {
                if (_depthForms[i].Width == width && _depthForms[i].Height == height &&
                    _depthForms[i].Format == format && _depthForms[i].Scaled == scaled)
                {
                    _depthForms[i].Count++;

                    return;
                }
            }

            if (_depthFormCount < _depthForms.Length)
            {
                _depthForms[_depthFormCount++] = new DepthForm
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    Scaled = scaled,
                    Count = 1,
                };
            }
            else
            {
                _depthFormOverflow++;
            }
        }

        private static void RecordColorForm(int width, int height, Ryujinx.Graphics.GAL.Format format)
        {
            for (int i = 0; i < _colorFormCount; i++)
            {
                if (_colorForms[i].Width == width && _colorForms[i].Height == height &&
                    _colorForms[i].Format == format)
                {
                    _colorForms[i].Count++;

                    return;
                }
            }

            if (_colorFormCount < _colorForms.Length)
            {
                _colorForms[_colorFormCount++] = new DepthForm
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    Count = 1,
                };
            }
            else
            {
                _colorFormOverflow++;
            }
        }

        private static void FlushPassWindow()
        {
            System.Text.StringBuilder sb = new();
            sb.Append($"MVPPSCAN passes: draws {_draws}, with-depth {_drawsWithDepth}, non-square-depth {_drawsNonSquareDepth}; forms:");

            if (_depthFormCount == 0)
            {
                sb.Append(" (none)");
            }

            for (int i = 0; i < _depthFormCount; i++)
            {
                ref DepthForm f = ref _depthForms[i];
                sb.Append($" {f.Width}x{f.Height} {f.Format}{(f.Scaled ? " scaled" : "")} x{f.Count};");
            }

            if (_depthFormOverflow > 0)
            {
                sb.Append($" (+{_depthFormOverflow} draws on overflow forms)");
            }

            sb.Append(" | COULEUR:");

            if (_colorFormCount == 0)
            {
                sb.Append(" (none)");
            }

            for (int i = 0; i < _colorFormCount; i++)
            {
                ref DepthForm f = ref _colorForms[i];
                sb.Append($" {f.Width}x{f.Height} {f.Format} x{f.Count};");
            }

            if (_colorFormOverflow > 0)
            {
                sb.Append($" (+{_colorFormOverflow})");
            }

            sb.Append($" | deep: proj {_lastProjCount}, view {_lastViewCount} (kept {_lastViewKept}), " +
                      $"vp-products {_lastVpProductCount}, canonical {_lastCanonicalCount}, " +
                      $"standalone-vp {_lastStandaloneVp} (+{_lastStandaloneSquare} square-rejected) (scans {_deepScans}).");

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());

            if (_liveMainH > 0)
            {
                _mainAspect = (float)_liveMainW / _liveMainH;
            }

            _liveMainArea = 0;
            _liveMainW = 0;
            _liveMainH = 0;

            _draws = 0;
            _drawsWithDepth = 0;
            _drawsNonSquareDepth = 0;
            _depthFormCount = 0;
            _depthFormOverflow = 0;
            _colorFormCount = 0;
            _colorFormOverflow = 0;
            _deepScans = 0;
        }

        private enum MatClass
        {
            Proj,
            ProjT,
            View,
            ViewT,
        }

        private struct Candidate
        {
            public int Stage;
            public int Slot;
            public int Offset;
            public MatClass Class;
            public float A;
            public float B;
            public float C;
            public bool InRun;
            public bool Identity;
        }

        private struct BufRef
        {
            public int Stage;
            public int Slot;
        }

        private static readonly Candidate[] _cands = new Candidate[MaxCandidates];
        private static int _candCount;

        private static readonly BufRef[] _bufs = new BufRef[MaxBuffers];
        private static int _bufCount;

        private static readonly float[] _products = new float[MaxProducts * 16];
        private static readonly int[] _prodProjIdx = new int[MaxProducts];
        private static readonly int[] _prodViewIdx = new int[MaxProducts];
        private static readonly bool[] _prodProjFirst = new bool[MaxProducts];
        private static int _prodCount;

        private static readonly float[] _tmpA = new float[16];
        private static readonly float[] _tmpB = new float[16];
        private static readonly float[] _tmpT = new float[16];

        private struct VpSolo
        {
            public int Stage;
            public int Slot;
            public int Offset;
            public bool Transposed;
            public float Fx;
            public float Aspect;
            public float X;
            public float Y;
            public float Z;
            public bool AspectOk;
            public float Motion;
            public float Straightness;

            public System.Numerics.Matrix4x4 M;

            public ulong Address;
        }

        private const int MaxVpSolos = 128;
        private static readonly VpSolo[] _vpSolos = new VpSolo[MaxVpSolos];
        private static int _vpSoloCount;

        private const float PosAgreeEps = 0.5f;

        private static bool _hasLock;
        private static ulong _lockAddress;
        private static int _lockOffset;

        private struct Tracked
        {
            public ulong Address;
            public int Offset;
            public float LastX;
            public float LastY;
            public float LastZ;
            public bool HasLast;
            public float MaxStep;
            public int Seen;

            public int RingHead;
            public int RingCount;
            public float PathLength;
        }

        private const int MaxTracks = 64;
        private const int RingSize = 6;

        private static readonly Tracked[] _tracks = new Tracked[MaxTracks];
        private static int _trackCount;

        private static readonly float[] _ringX = new float[MaxTracks * RingSize];
        private static readonly float[] _ringY = new float[MaxTracks * RingSize];
        private static readonly float[] _ringZ = new float[MaxTracks * RingSize];
        private static readonly float[] _ringStep = new float[MaxTracks * RingSize];

        private static void DeepScan(GpuChannel channel, Image.Texture drawDs)
        {
            _deepScans++;
            _candCount = 0;
            _bufCount = 0;
            _prodCount = 0;
            _vpSoloCount = 0;

            int canonical = 0;
            int standaloneVp = 0;
            _lastStandaloneSquare = 0;

            ulong layoutFp = 14695981039346656037UL;
            ulong foundFp = 14695981039346656037UL;

            System.Text.StringBuilder detail = null;
            int detailLines = 0;

            for (int stage = 0; stage < Constants.ShaderStages; stage++)
            {
                uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);

                for (int slot = 0; mask != 0; slot++, mask >>= 1)
                {
                    if ((mask & 1) == 0)
                    {
                        continue;
                    }

                    if (!TryReadBuffer(channel, stage, slot, out ReadOnlySpan<float> data))
                    {
                        continue;
                    }

                    layoutFp = FnvStep(layoutFp, (ulong)((stage << 24) | (slot << 16) | data.Length));

                    if (_bufCount < _bufs.Length)
                    {
                        _bufs[_bufCount++] = new BufRef { Stage = stage, Slot = slot };
                    }

                    int firstCand = _candCount;

                    for (int i = 0; i + 16 <= data.Length && _candCount < MaxCandidates; i += 4)
                    {
                        ReadOnlySpan<float> m = data.Slice(i, 16);

                        MatClass cls;
                        float a, b, c;
                        bool identity = false;

                        if (IsPerspectiveProj(m))
                        {
                            cls = MatClass.Proj; a = m[0]; b = m[5]; c = 0f;
                        }
                        else if (IsOrthonormalView(m))
                        {
                            cls = MatClass.View;
                            ViewPos(m, out a, out b, out c);
                            identity = IsNearIdentity(m);
                        }
                        else
                        {
                            Transpose(m, _tmpT);
                            ReadOnlySpan<float> t = _tmpT;

                            if (IsPerspectiveProj(t))
                            {
                                cls = MatClass.ProjT; a = t[0]; b = t[5]; c = 0f;
                            }
                            else if (IsOrthonormalView(t))
                            {
                                cls = MatClass.ViewT;
                                ViewPos(t, out a, out b, out c);
                                identity = IsNearIdentity(t);
                            }
                            else
                            {
                                continue;
                            }
                        }

                        _cands[_candCount++] = new Candidate
                        {
                            Stage = stage,
                            Slot = slot,
                            Offset = i * 4,
                            Class = cls,
                            A = a,
                            B = b,
                            C = c,
                            Identity = identity,
                        };

                        foundFp = FnvStep(foundFp, (uint)((stage << 28) | (slot << 20) | ((i * 4) << 4) | (int)cls));
                    }

                    MarkRuns(firstCand);

                    for (int i = 0; i + 16 <= data.Length; i += 4)
                    {
                        ReadOnlySpan<float> m = data.Slice(i, 16);
                        bool transposed = false;

                        if (!IsViewProj(m, out float fx, out float fy))
                        {
                            Transpose(m, _tmpT);

                            if (!IsViewProj(_tmpT, out fx, out fy))
                            {
                                continue;
                            }

                            transposed = true;
                        }

                        ReadOnlySpan<float> vpm = transposed ? _tmpT : m;

                        if (MathF.Abs(fy / fx - 1f) < 0.2f)
                        {
                            _lastStandaloneSquare++;

                            continue;
                        }

                        standaloneVp++;
                        foundFp = FnvStep(foundFp,
                            0xD00000000000UL | (uint)((stage << 20) | (slot << 16) | (i * 4)));

                        ViewProjPos(vpm, fx, fy, out float px, out float py, out float pz);

                        if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz) ||
                            MathF.Abs(px) > 1e6f || MathF.Abs(py) > 1e6f || MathF.Abs(pz) > 1e6f)
                        {
                            standaloneVp--;

                            continue;
                        }

                        if (_vpSoloCount < MaxVpSolos)
                        {
                            _vpSolos[_vpSoloCount++] = new VpSolo
                            {
                                Stage = stage,
                                Slot = slot,
                                Offset = i * 4,
                                Transposed = transposed,
                                Fx = fx,
                                Aspect = fy / fx,
                                X = px,
                                Y = py,
                                Z = pz,
                                Address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot),
                                M = new System.Numerics.Matrix4x4(
                                    vpm[0], vpm[1], vpm[2], vpm[3],
                                    vpm[4], vpm[5], vpm[6], vpm[7],
                                    vpm[8], vpm[9], vpm[10], vpm[11],
                                    vpm[12], vpm[13], vpm[14], vpm[15]),
                            };
                        }

                        AppendDetail(ref detail, ref detailLines,
                            $"VP-SOLO{(transposed ? "^T" : "  ")} stage{stage} cbuf{slot} +0x{i * 4:X3} " +
                            $"@{channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot):X10} " +
                            $"fx={fx:0.###} fy={fy:0.###} aspect={fy / fx:0.###} campos=[{px:0.#} {py:0.#} {pz:0.#}]");
                    }

                    for (int i = 0; i + 48 <= data.Length; i += 4)
                    {
                        if (IsOrthonormalView(data.Slice(i, 16)) &&
                            IsPerspectiveProj(data.Slice(i + 16, 16)) &&
                            ProductMatches(data.Slice(i + 16, 16), data.Slice(i, 16), data.Slice(i + 32, 16)))
                        {
                            canonical++;
                            foundFp = FnvStep(foundFp, 0xCA0000000000UL | (uint)((stage << 20) | (slot << 16) | (i * 4)));

                            AppendDetail(ref detail, ref detailLines,
                                $"canonical [view][proj][vp] at stage{stage} cbuf{slot} +0x{i * 4:X3}");
                        }
                    }
                }
            }

            int projCount = 0;
            int viewCount = 0;
            int viewKept = 0;

            for (int i = 0; i < _candCount; i++)
            {
                bool isProj = _cands[i].Class == MatClass.Proj || _cands[i].Class == MatClass.ProjT;

                if (isProj)
                {
                    projCount++;
                }
                else
                {
                    viewCount++;

                    if (!_cands[i].Identity && !_cands[i].InRun)
                    {
                        viewKept++;
                    }
                }
            }

            BuildProducts(channel);

            int vpProducts = SearchProducts(channel, ref detail, ref detailLines, ref foundFp);

            if (layoutFp != _lastCbufLayoutFp)
            {
                _lastCbufLayoutFp = layoutFp;
                LogCbufLayout(channel);
            }

            _lastProjCount = projCount;
            _lastViewCount = viewCount;
            _lastViewKept = viewKept;
            _lastVpProductCount = vpProducts;
            _lastCanonicalCount = canonical;
            _lastStandaloneVp = standaloneVp;

            System.Text.StringBuilder verdict = null;
            int verdictLines = 0;
            LogVerdict(ref verdict, ref verdictLines);

            if (verdict != null)
            {
                Logger.Info?.Print(LogClass.Gpu, $"MVPPSCAN {verdict.ToString().TrimStart()}");
            }

            if (foundFp != _lastFoundSetFp)
            {
                _lastFoundSetFp = foundFp;

                for (int i = 0; i < _candCount && detailLines < MaxDetailLines; i++)
                {
                    ref Candidate c = ref _cands[i];

                    if (c.Class != MatClass.Proj && c.Class != MatClass.ProjT)
                    {
                        continue;
                    }

                    AppendDetail(ref detail, ref detailLines,
                        $"{(c.Class == MatClass.ProjT ? "PROJ^T" : "PROJ  ")} stage{c.Stage} cbuf{c.Slot} +0x{c.Offset:X3} " +
                        $"fx={c.A:0.###} fy={c.B:0.###} aspect={(c.A > 0 ? c.B / c.A : 0):0.###}");
                }

                for (int i = 0; i < _candCount && detailLines < MaxDetailLines; i++)
                {
                    ref Candidate c = ref _cands[i];

                    if (c.Class != MatClass.View && c.Class != MatClass.ViewT)
                    {
                        continue;
                    }

                    if (c.Identity || c.InRun)
                    {
                        continue;
                    }

                    AppendDetail(ref detail, ref detailLines,
                        $"{(c.Class == MatClass.ViewT ? "VIEW^T" : "VIEW  ")} stage{c.Stage} cbuf{c.Slot} +0x{c.Offset:X3} " +
                        $"pos=[{c.A:0.#} {c.B:0.#} {c.C:0.#}]");
                }

                string head =
                    $"MVPPSCAN deep #{_deepScans}: proj {projCount}, view {viewCount} (kept {viewKept} after " +
                    $"identity/palette filter), vp-products {vpProducts}, canonical {canonical}, " +
                    $"draw-ds {(drawDs != null ? $"{drawDs.Info.Width}x{drawDs.Info.Height}" : "none")}";

                Logger.Info?.Print(LogClass.Gpu,
                    detail != null ? $"{head}:\n{detail}" : $"{head} -- no candidate survived.");
            }
        }

        private static float TrackMotion(in VpSolo c, out float straightness)
        {
            straightness = 1f;

            int idx = -1;

            for (int i = 0; i < _trackCount; i++)
            {
                if (_tracks[i].Address == c.Address && _tracks[i].Offset == c.Offset)
                {
                    idx = i;

                    break;
                }
            }

            if (idx < 0)
            {
                if (_trackCount >= _tracks.Length)
                {
                    return 0f;
                }

                idx = _trackCount++;
                _tracks[idx] = new Tracked { Address = c.Address, Offset = c.Offset };
            }

            ref Tracked t = ref _tracks[idx];
            t.Seen++;

            t.MaxStep *= 0.5f;

            if (t.HasLast)
            {
                float dx = c.X - t.LastX;
                float dy = c.Y - t.LastY;
                float dz = c.Z - t.LastZ;
                float step = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                if (float.IsFinite(step) && step < 1e5f && step > t.MaxStep)
                {
                    t.MaxStep = step;
                }
            }

            int slot = idx * RingSize;

            if (t.RingCount == RingSize)
            {
                t.PathLength -= _ringStep[slot + t.RingHead];
            }

            float lastStep = 0f;

            if (t.HasLast)
            {
                float ddx = c.X - t.LastX;
                float ddy = c.Y - t.LastY;
                float ddz = c.Z - t.LastZ;
                lastStep = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

                if (!float.IsFinite(lastStep) || lastStep > 1e5f)
                {
                    lastStep = 0f;
                }
            }

            _ringX[slot + t.RingHead] = c.X;
            _ringY[slot + t.RingHead] = c.Y;
            _ringZ[slot + t.RingHead] = c.Z;
            _ringStep[slot + t.RingHead] = lastStep;
            t.PathLength += lastStep;
            t.RingHead = (t.RingHead + 1) % RingSize;

            if (t.RingCount < RingSize)
            {
                t.RingCount++;
            }

            if (t.RingCount >= 3 && t.PathLength > 1e-4f)
            {
                int oldest = (t.RingHead - t.RingCount + RingSize * 2) % RingSize;
                float nx = c.X - _ringX[slot + oldest];
                float ny = c.Y - _ringY[slot + oldest];
                float nz = c.Z - _ringZ[slot + oldest];
                float net = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                straightness = net / t.PathLength;
            }

            t.LastX = c.X;
            t.LastY = c.Y;
            t.LastZ = c.Z;
            t.HasLast = true;

            return t.MaxStep;
        }

        private static void LogVerdict(ref System.Text.StringBuilder detail, ref int detailLines)
        {
            if (_vpSoloCount == 0)
            {
                return;
            }

            float targetAspect = _mainAspect;

            float bestMotion = 0f;
            float bestStraight = 0f;
            int considered = 0;

            for (int i = 0; i < _vpSoloCount; i++)
            {
                ref VpSolo a = ref _vpSolos[i];

                a.AspectOk = targetAspect <= 0f || MathF.Abs(a.Aspect / targetAspect - 1f) <= 0.05f;

                if (!a.AspectOk)
                {
                    continue;
                }

                considered++;
                a.Motion = TrackMotion(in a, out float straight);
                a.Straightness = straight;

                if (a.Motion > bestMotion)
                {
                    bestMotion = a.Motion;
                }

                if (a.Motion > 0f && straight > bestStraight)
                {
                    bestStraight = straight;
                }
            }

            int elected = -1;
            int electedVotes = 0;
            int lockedIdx = -1;
            int lockedVotes = 0;
            int movers = 0;

            for (int i = 0; i < _vpSoloCount; i++)
            {
                ref VpSolo a = ref _vpSolos[i];

                if (!a.AspectOk)
                {
                    continue;
                }

                if (bestMotion > 0f && a.Motion < bestMotion * 0.1f)
                {
                    continue;
                }

                if (bestStraight > 0f && a.Straightness < bestStraight * 0.35f)
                {
                    continue;
                }

                movers++;

                int votes = 0;

                for (int j = 0; j < _vpSoloCount; j++)
                {
                    ref VpSolo b = ref _vpSolos[j];

                    if (!b.AspectOk ||
                        MathF.Abs(b.X - a.X) > PosAgreeEps ||
                        MathF.Abs(b.Y - a.Y) > PosAgreeEps ||
                        MathF.Abs(b.Z - a.Z) > PosAgreeEps)
                    {
                        continue;
                    }

                    bool alreadyCounted = false;

                    for (int k = 0; k < j; k++)
                    {
                        ref VpSolo c = ref _vpSolos[k];

                        if (c.AspectOk && c.Address == b.Address && c.Offset == b.Offset &&
                            MathF.Abs(c.X - a.X) <= PosAgreeEps &&
                            MathF.Abs(c.Y - a.Y) <= PosAgreeEps &&
                            MathF.Abs(c.Z - a.Z) <= PosAgreeEps)
                        {
                            alreadyCounted = true;

                            break;
                        }
                    }

                    if (!alreadyCounted)
                    {
                        votes++;
                    }
                }

                if (votes > electedVotes)
                {
                    electedVotes = votes;
                    elected = i;
                }

                if (_hasLock && a.Address == _lockAddress && a.Offset == _lockOffset)
                {
                    lockedIdx = i;
                    lockedVotes = votes;
                }
            }

            if (_hasLock && lockedIdx < 0)
            {
                _hasLock = false;
            }

            int winner;
            string mode;

            if (lockedIdx >= 0)
            {
                winner = lockedIdx;
                mode = "VERROU";
            }
            else if (elected >= 0)
            {
                winner = elected;

                bool trustworthy = targetAspect > 0f && bestMotion > 0f && _lastScanOnMainPass;

                mode = trustworthy ? (_hasLock ? "RE-ELU" : "ELU") : "PROVISOIRE";

                if (trustworthy)
                {
                    _hasLock = true;
                    _lockAddress = _vpSolos[elected].Address;
                    _lockOffset = _vpSolos[elected].Offset;
                }
            }
            else
            {
                AppendDetail(ref detail, ref detailLines,
                    $"VERDICT: aucun candidat retenu ({_vpSoloCount} bruts, {considered} au bon aspect, " +
                    $"{movers} mobiles, cible aspect {targetAspect:0.###}, meilleur mouvement {bestMotion:0.##})");

                return;
            }

            ref VpSolo w = ref _vpSolos[winner];

            if (bestMotion <= 0f)
            {
                mode = "CHAUFFE";
            }

            string pass = _lastScanOnMainPass ? "" : " [hors passe scene]";

            string divergence = lockedIdx >= 0 && elected >= 0 && elected != lockedIdx && electedVotes > lockedVotes
                ? $" | DIVERGENCE: le consensus dirait stage{_vpSolos[elected].Stage} cbuf{_vpSolos[elected].Slot} " +
                  $"+0x{_vpSolos[elected].Offset:X3} campos=[{_vpSolos[elected].X:0.#} {_vpSolos[elected].Y:0.#} " +
                  $"{_vpSolos[elected].Z:0.#}] ({electedVotes} voix contre {lockedVotes})"
                : "";

            AppendDetail(ref detail, ref detailLines,
                $"VERDICT [{mode}]: camera = stage{w.Stage} cbuf{w.Slot} +0x{w.Offset:X3}{(w.Transposed ? "^T" : "")} " +
                $"@{w.Address:X10} rect={w.Straightness:0.##} " +
                $"campos=[{w.X:0.#} {w.Y:0.#} {w.Z:0.#}] fx={w.Fx:0.###} aspect={w.Aspect:0.###} " +
                $"mouvement={w.Motion:0.##} -- {(lockedIdx >= 0 ? lockedVotes : electedVotes)} sources d'accord, " +
                $"{movers} mobiles sur {considered} au bon aspect ({_vpSoloCount} bruts){pass}{divergence}");

            if (MvppSoloCamera.TryGetPublishedForProbe(out System.Numerics.Matrix4x4 pubVp,
                    out float pubX, out float pubY, out float pubZ))
            {
                float ddx = w.X - pubX;
                float ddy = w.Y - pubY;
                float ddz = w.Z - pubZ;
                float dpos = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

                float drot = 0f;
                drot = MathF.Max(drot, MathF.Abs(w.M.M11 - pubVp.M11));
                drot = MathF.Max(drot, MathF.Abs(w.M.M12 - pubVp.M12));
                drot = MathF.Max(drot, MathF.Abs(w.M.M13 - pubVp.M13));
                drot = MathF.Max(drot, MathF.Abs(w.M.M21 - pubVp.M21));
                drot = MathF.Max(drot, MathF.Abs(w.M.M22 - pubVp.M22));
                drot = MathF.Max(drot, MathF.Abs(w.M.M23 - pubVp.M23));
                drot = MathF.Max(drot, MathF.Abs(w.M.M31 - pubVp.M31));
                drot = MathF.Max(drot, MathF.Abs(w.M.M32 - pubVp.M32));
                drot = MathF.Max(drot, MathF.Abs(w.M.M33 - pubVp.M33));

                float wNorm = MathF.Sqrt(w.X * w.X + w.Y * w.Y + w.Z * w.Z);
                bool bornesOk = dpos < 150f && wNorm >= 5f;
                bool diverge = bornesOk && (dpos > 3f || drot > 0.1f);

                _dgConsecutive = diverge ? _dgConsecutive + 1 : 0;

                AppendDetail(ref detail, ref detailLines,
                    $"DIVERG: dpos={dpos:0.##} drot={drot:0.####} " +
                    $"pub=[{pubX:0.#} {pubY:0.#} {pubZ:0.#}] " +
                    $"bornes={(bornesOk ? "OK" : "HORS")} n={_dgConsecutive}");
            }
        }

        private static int _dgConsecutive;

        private static bool TryReadBuffer(GpuChannel channel, int stage, int slot, out ReadOnlySpan<float> data)
        {
            data = default;

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);
            int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot), MaxBytesPerBuffer);

            if (address == 0 || address == ulong.MaxValue || size < 64)
            {
                return false;
            }

            try
            {
                data = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, size));
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static void MarkRuns(int firstCand)
        {
            for (int i = firstCand; i < _candCount; i++)
            {
                if (_cands[i].Class != MatClass.View && _cands[i].Class != MatClass.ViewT)
                {
                    continue;
                }

                int chain = 1;

                for (int j = firstCand; j < _candCount; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    if (_cands[j].Class != MatClass.View && _cands[j].Class != MatClass.ViewT)
                    {
                        continue;
                    }

                    int delta = _cands[j].Offset - _cands[i].Offset;

                    if (delta % 64 == 0 && Math.Abs(delta) <= 192)
                    {
                        chain++;
                    }
                }

                if (chain >= 3)
                {
                    _cands[i].InRun = true;
                }
            }
        }

        private static void BuildProducts(GpuChannel channel)
        {
            int projUsed = 0;

            for (int pi = 0; pi < _candCount && projUsed < MaxProjPaired && _prodCount + 2 <= MaxProducts; pi++)
            {
                if (_cands[pi].Class != MatClass.Proj && _cands[pi].Class != MatClass.ProjT)
                {
                    continue;
                }

                if (!LoadCanonical(channel, _cands[pi], _tmpA))
                {
                    continue;
                }

                projUsed++;
                int viewUsed = 0;

                for (int vi = 0; vi < _candCount && viewUsed < MaxViewPaired && _prodCount + 2 <= MaxProducts; vi++)
                {
                    if (_cands[vi].Class != MatClass.View && _cands[vi].Class != MatClass.ViewT)
                    {
                        continue;
                    }

                    if (_cands[vi].Identity || _cands[vi].InRun)
                    {
                        continue;
                    }

                    if (!LoadCanonical(channel, _cands[vi], _tmpB))
                    {
                        continue;
                    }

                    viewUsed++;

                    Multiply(_tmpA, _tmpB, _products.AsSpan(_prodCount * 16, 16));
                    _prodProjIdx[_prodCount] = pi;
                    _prodViewIdx[_prodCount] = vi;
                    _prodProjFirst[_prodCount] = true;
                    _prodCount++;

                    Multiply(_tmpB, _tmpA, _products.AsSpan(_prodCount * 16, 16));
                    _prodProjIdx[_prodCount] = pi;
                    _prodViewIdx[_prodCount] = vi;
                    _prodProjFirst[_prodCount] = false;
                    _prodCount++;
                }
            }
        }

        private static int SearchProducts(
            GpuChannel channel,
            ref System.Text.StringBuilder detail,
            ref int detailLines,
            ref ulong foundFp)
        {
            if (_prodCount == 0)
            {
                return 0;
            }

            int found = 0;

            for (int b = 0; b < _bufCount; b++)
            {
                if (!TryReadBuffer(channel, _bufs[b].Stage, _bufs[b].Slot, out ReadOnlySpan<float> data))
                {
                    continue;
                }

                for (int i = 0; i + 16 <= data.Length; i += 4)
                {
                    ReadOnlySpan<float> w = data.Slice(i, 16);

                    for (int k = 0; k < _prodCount; k++)
                    {
                        ReadOnlySpan<float> exp = _products.AsSpan(k * 16, 16);

                        bool direct = Close(w[0], exp[0]) && Close(w[5], exp[5]) && MatchesLoose(w, exp, false);
                        bool transposed = !direct &&
                            Close(w[0], exp[0]) && Close(w[5], exp[5]) && MatchesLoose(w, exp, true);

                        if (!direct && !transposed)
                        {
                            continue;
                        }

                        found++;

                        ref Candidate p = ref _cands[_prodProjIdx[k]];
                        ref Candidate v = ref _cands[_prodViewIdx[k]];

                        AppendDetail(ref detail, ref detailLines,
                            $"*** VP FOUND: {p.Class}(stage{p.Stage} cbuf{p.Slot} +0x{p.Offset:X3}) " +
                            $"{(_prodProjFirst[k] ? "x" : "<-x-")} {v.Class}(stage{v.Stage} cbuf{v.Slot} +0x{v.Offset:X3}) " +
                            $"= stage{_bufs[b].Stage} cbuf{_bufs[b].Slot} +0x{i * 4:X3}{(transposed ? " (stored transposed)" : "")} " +
                            $"| campos=[{v.A:0.#} {v.B:0.#} {v.C:0.#}] fx={p.A:0.###}");

                        foundFp = FnvStep(foundFp,
                            0xF00000000000UL | (uint)((_bufs[b].Stage << 20) | (_bufs[b].Slot << 16) | (i * 4)));

                        break;
                    }
                }
            }

            return found;
        }

        private static bool Close(float a, float b)
        {
            return MathF.Abs(a - b) <= MathF.Max(0.02f, MathF.Abs(b) * 0.01f);
        }

        private static bool LoadCanonical(GpuChannel channel, in Candidate c, float[] dst)
        {
            if (!TryReadBuffer(channel, c.Stage, c.Slot, out ReadOnlySpan<float> data))
            {
                return false;
            }

            int idx = c.Offset / 4;

            if (idx + 16 > data.Length)
            {
                return false;
            }

            ReadOnlySpan<float> src = data.Slice(idx, 16);

            if (c.Class == MatClass.ProjT || c.Class == MatClass.ViewT)
            {
                Transpose(src, dst);
            }
            else
            {
                src.CopyTo(dst);
            }

            return true;
        }

        private static bool IsViewProj(ReadOnlySpan<float> m, out float fx, out float fy)
        {
            fx = 0f;
            fy = 0f;

            float n3 = MathF.Sqrt(m[12] * m[12] + m[13] * m[13] + m[14] * m[14]);

            if (MathF.Abs(n3 - 1f) > 0.02f)
            {
                return false;
            }

            float n0 = MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            float n1 = MathF.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);

            if (!float.IsFinite(n0) || !float.IsFinite(n1) ||
                n0 < 0.05f || n1 < 0.05f || n0 > 50f || n1 > 50f)
            {
                return false;
            }

            float d01 = (m[0] * m[4] + m[1] * m[5] + m[2] * m[6]) / (n0 * n1);
            float d03 = (m[0] * m[12] + m[1] * m[13] + m[2] * m[14]) / n0;
            float d13 = (m[4] * m[12] + m[5] * m[13] + m[6] * m[14]) / n1;

            if (MathF.Abs(d01) > 0.02f || MathF.Abs(d03) > 0.02f || MathF.Abs(d13) > 0.02f)
            {
                return false;
            }

            float n2 = MathF.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);

            if (n2 > 1e-4f)
            {
                float cx = m[9] * m[14] - m[10] * m[13];
                float cy = m[10] * m[12] - m[8] * m[14];
                float cz = m[8] * m[13] - m[9] * m[12];

                if (MathF.Sqrt(cx * cx + cy * cy + cz * cz) / n2 > 0.02f)
                {
                    return false;
                }
            }

            bool rotIsIdentity =
                MathF.Abs(m[0] / n0 - 1f) < 1e-3f && MathF.Abs(m[1]) < 1e-3f && MathF.Abs(m[2]) < 1e-3f &&
                MathF.Abs(m[4]) < 1e-3f && MathF.Abs(m[5] / n1 - 1f) < 1e-3f && MathF.Abs(m[6]) < 1e-3f &&
                MathF.Abs(m[12]) < 1e-3f && MathF.Abs(m[13]) < 1e-3f;

            bool noTranslation =
                MathF.Abs(m[3]) < 1e-3f && MathF.Abs(m[7]) < 1e-3f && MathF.Abs(m[15]) < 1e-3f;

            if (rotIsIdentity && noTranslation)
            {
                return false;
            }

            fx = n0;
            fy = n1;

            return true;
        }

        private static void ViewProjPos(ReadOnlySpan<float> m, float fx, float fy, out float x, out float y, out float z)
        {
            float r00 = m[0] / fx, r01 = m[1] / fx, r02 = m[2] / fx;
            float r10 = m[4] / fy, r11 = m[5] / fy, r12 = m[6] / fy;
            float r20 = m[12], r21 = m[13], r22 = m[14];

            float tx = m[3] / fx;
            float ty = m[7] / fy;
            float tz = m[15];

            x = -(r00 * tx + r10 * ty + r20 * tz);
            y = -(r01 * tx + r11 * ty + r21 * tz);
            z = -(r02 * tx + r12 * ty + r22 * tz);
        }

        private static void ViewPos(ReadOnlySpan<float> m, out float x, out float y, out float z)
        {
            x = -(m[0] * m[3] + m[4] * m[7] + m[8] * m[11]);
            y = -(m[1] * m[3] + m[5] * m[7] + m[9] * m[11]);
            z = -(m[2] * m[3] + m[6] * m[7] + m[10] * m[11]);
        }

        private static bool IsNearIdentity(ReadOnlySpan<float> m)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float expected = r == c ? 1f : 0f;

                    if (MathF.Abs(m[r * 4 + c] - expected) > 1e-3f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void Transpose(ReadOnlySpan<float> m, Span<float> dst)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    dst[r * 4 + c] = m[c * 4 + r];
                }
            }
        }

        private static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float sum = 0f;

                    for (int k = 0; k < 4; k++)
                    {
                        sum += a[r * 4 + k] * b[k * 4 + c];
                    }

                    dst[r * 4 + c] = sum;
                }
            }
        }

        private static bool MatchesLoose(ReadOnlySpan<float> w, ReadOnlySpan<float> expected, bool transposed)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float e = expected[r * 4 + c];
                    float g = transposed ? w[c * 4 + r] : w[r * 4 + c];
                    float tolerance = MathF.Max(0.02f, MathF.Abs(e) * 0.01f);

                    if (MathF.Abs(g - e) > tolerance)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void AppendDetail(ref System.Text.StringBuilder detail, ref int lines, string line)
        {
            if (lines >= MaxDetailLines)
            {
                return;
            }

            detail ??= new System.Text.StringBuilder();

            if (detail.Length > 0)
            {
                detail.Append('\n');
            }

            detail.Append("  ").Append(line);
            lines++;
        }

        private static void LogCbufLayout(GpuChannel channel)
        {
            System.Text.StringBuilder sb = new("MVPPSCAN cbufs:");

            for (int stage = 0; stage < Constants.ShaderStages; stage++)
            {
                uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);

                if (mask == 0)
                {
                    continue;
                }

                sb.Append($" stage{stage}[");
                bool first = true;

                for (int slot = 0; mask != 0; slot++, mask >>= 1)
                {
                    if ((mask & 1) == 0)
                    {
                        continue;
                    }

                    int size = channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot);
                    sb.Append($"{(first ? "" : " ")}c{slot}:{size}");
                    first = false;
                }

                sb.Append(']');
            }

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());
        }

        private static ulong FnvStep(ulong fp, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                fp = (fp ^ ((value >> (i * 8)) & 0xFF)) * 1099511628211UL;
            }

            return fp;
        }

        private static bool IsOrthonormalView(ReadOnlySpan<float> m)
        {
            if (MathF.Abs(m[12]) > 1e-3f || MathF.Abs(m[13]) > 1e-3f ||
                MathF.Abs(m[14]) > 1e-3f || MathF.Abs(m[15] - 1f) > 1e-3f)
            {
                return false;
            }

            for (int r = 0; r < 3; r++)
            {
                float len = m[r * 4] * m[r * 4] + m[r * 4 + 1] * m[r * 4 + 1] + m[r * 4 + 2] * m[r * 4 + 2];

                if (MathF.Abs(len - 1f) > 0.02f)
                {
                    return false;
                }
            }

            for (int a = 0; a < 3; a++)
            {
                for (int b = a + 1; b < 3; b++)
                {
                    float dot = m[a * 4] * m[b * 4] + m[a * 4 + 1] * m[b * 4 + 1] + m[a * 4 + 2] * m[b * 4 + 2];

                    if (MathF.Abs(dot) > 0.02f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsPerspectiveProj(ReadOnlySpan<float> m)
        {
            return m[0] > 0.05f && MathF.Abs(m[1]) < 1e-4f && MathF.Abs(m[2]) < 1e-4f && MathF.Abs(m[3]) < 1e-4f &&
                   MathF.Abs(m[4]) < 1e-4f && m[5] > 0.05f && MathF.Abs(m[6]) < 1e-4f && MathF.Abs(m[7]) < 1e-4f &&
                   MathF.Abs(m[8]) < 1e-4f && MathF.Abs(m[9]) < 1e-4f &&
                   MathF.Abs(m[12]) < 1e-4f && MathF.Abs(m[13]) < 1e-4f &&
                   MathF.Abs(MathF.Abs(m[14]) - 1f) < 0.01f &&
                   MathF.Abs(m[15]) < 1e-3f;
        }

        private static bool ProductMatches(ReadOnlySpan<float> p, ReadOnlySpan<float> v, ReadOnlySpan<float> vp)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float expected = 0f;

                    for (int k = 0; k < 4; k++)
                    {
                        expected += p[r * 4 + k] * v[k * 4 + c];
                    }

                    float tolerance = MathF.Max(0.02f, MathF.Abs(expected) * 0.01f);

                    if (MathF.Abs(vp[r * 4 + c] - expected) > tolerance)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
