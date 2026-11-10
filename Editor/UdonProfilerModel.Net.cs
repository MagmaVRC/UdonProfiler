using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MagmaVRC.UdonProfiler
{
    /// <summary>Networking totals of one behaviour over a frame range; byte counts are estimates.</summary>
    public sealed class NetRow
    {
        public int behaviourId;
        public string script, objectName, objectPath;
        public NetSync sync;
        public long requests, ignored, serializations, overLimit, estBytes;
        public long eventsAll, eventsOwner, eventsOthers, eventsSelf, eventBytes;
        public long rateLimitHits, transfers;
        /// <summary>Recorded seconds of the range the rates are taken over.</summary>
        public double seconds;

        internal int mark;

        public long Events => eventsAll + eventsOwner + eventsOthers + eventsSelf;
        public double RequestsPerSec => Rate(requests);
        public double SerializationsPerSec => Rate(serializations);
        public double BytesPerSerialization => serializations > 0 ? (double)estBytes / serializations : 0;
        public double SerializationBytesPerSec => Rate(estBytes);
        public double EventsPerSec => Rate(Events);
        public double EventBytesPerSec => Rate(eventBytes);
        public double BytesPerSec => Rate(estBytes + eventBytes);

        double Rate(long n) => seconds > 0 ? n / seconds : 0;
    }

    /// <summary>Networking totals of a frame range.</summary>
    public sealed class NetSummary
    {
        public readonly List<NetRow> rows = new List<NetRow>();
        public int frames;
        public double seconds;
        public double bytesPerSec;
        public bool HasData => rows.Count > 0;
    }

    public sealed partial class UdonProfilerModel
    {
        readonly NetSummary _net = new NetSummary();
        readonly Dictionary<int, NetRow> _netRows = new Dictionary<int, NetRow>();
        int _netMark, _netFrom = int.MinValue, _netTo, _netGen, _netVersion, _netMutation;

        /// <summary>Networking totals of the last build's range, or of [from, to]; returns the same model-owned instance every call.</summary>
        public NetSummary BuildNetwork(int from, int to)
        {
            var s = _net;
            int gen = UdonProfilerRecorder.Generation;
            var toFrame = UdonProfilerRecorder.GetFrame(to);
            bool sealedTo = toFrame != null && toFrame.isSealed;
            if (_netFrom == from && _netTo == to && _netGen == gen && (sealedTo || (_netVersion == UdonProfilerRecorder.Version && _netMutation == (toFrame?.mutation ?? 0))))
                return s;
            _netFrom = from; _netTo = to; _netGen = gen; _netVersion = UdonProfilerRecorder.Version; _netMutation = toFrame?.mutation ?? 0;
            if (gen != _netRowsGen) { _netRows.Clear(); _netRowsGen = gen; }
            _netMark++;
            s.rows.Clear();
            s.frames = 0;
            double seconds = 0;
            for (int i = from; i <= to; i++)
            {
                var f = UdonProfilerRecorder.GetFrame(i);
                if (f == null) continue;
                s.frames++;
                seconds += f.deltaTime;
                for (int k = 0; k < f.netCount; k++)
                {
                    ref readonly var n = ref f.net[k];
                    if (!_netRows.TryGetValue(n.behaviourId, out var r))
                    {
                        var info = UdonProfilerRecorder.GetInfo(n.behaviourId);
                        r = new NetRow { behaviourId = n.behaviourId, script = info?.script ?? "?", objectName = info?.objectName ?? "?", objectPath = info?.objectPath ?? "?", sync = info?.syncMode ?? NetSync.Unknown };
                        _netRows[n.behaviourId] = r;
                    }
                    if (r.mark != _netMark)
                    {
                        r.mark = _netMark;
                        r.requests = r.ignored = r.serializations = r.overLimit = r.estBytes = 0;
                        r.eventsAll = r.eventsOwner = r.eventsOthers = r.eventsSelf = r.eventBytes = 0;
                        r.rateLimitHits = r.transfers = 0;
                        s.rows.Add(r);
                    }
                    r.requests += n.requests; r.ignored += n.requestsIgnored; r.serializations += n.serializations; r.overLimit += n.overLimit; r.estBytes += n.estBytes;
                    r.eventsAll += n.eventsAll; r.eventsOwner += n.eventsOwner; r.eventsOthers += n.eventsOthers; r.eventsSelf += n.eventsSelf; r.eventBytes += n.eventBytes;
                    r.rateLimitHits += n.rateLimitHits; r.transfers += n.ownershipTransfers;
                }
            }
            if (seconds <= 0) seconds = s.frames / 60.0;
            s.seconds = seconds;
            double bytes = 0;
            foreach (var r in s.rows)
            {
                r.seconds = seconds;
                bytes += r.estBytes + r.eventBytes;
            }
            s.bytesPerSec = seconds > 0 ? bytes / seconds : 0;
            s.rows.Sort((a, b) => a.BytesPerSec != b.BytesPerSec ? b.BytesPerSec.CompareTo(a.BytesPerSec) : a.behaviourId.CompareTo(b.behaviourId));
            return s;
        }

        int _netRowsGen = int.MinValue;

        internal NetRow NetOf(int behaviourId)
        {
            if (!_netRows.TryGetValue(behaviourId, out var r) || r.mark != _netMark || _netGen != UdonProfilerRecorder.Generation) return null;
            return r;
        }

        internal NetSummary LastNetwork => _net;

        /// <summary>Writes the network table as CSV.</summary>
        public static string NetworkCsv(NetSummary s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("script,object,sync,requests,ignored,serializations,over_limit,est_bytes,events_all,events_owner,events_others,events_self,event_bytes,rate_limit_hits,transfers,seconds,requests_per_s,serializations_per_s,bytes_per_serialization,bytes_per_s");
            if (s == null) return sb.ToString();
            var ci = CultureInfo.InvariantCulture;
            foreach (var r in s.rows)
            {
                sb.Append(Q(r.script)).Append(',').Append(Q(r.objectPath)).Append(',').Append(r.sync).Append(',')
                  .Append(r.requests.ToString(ci)).Append(',').Append(r.ignored.ToString(ci)).Append(',').Append(r.serializations.ToString(ci)).Append(',')
                  .Append(r.overLimit.ToString(ci)).Append(',').Append(r.estBytes.ToString(ci)).Append(',')
                  .Append(r.eventsAll.ToString(ci)).Append(',').Append(r.eventsOwner.ToString(ci)).Append(',').Append(r.eventsOthers.ToString(ci)).Append(',')
                  .Append(r.eventsSelf.ToString(ci)).Append(',').Append(r.eventBytes.ToString(ci)).Append(',')
                  .Append(r.rateLimitHits.ToString(ci)).Append(',').Append(r.transfers.ToString(ci)).Append(',')
                  .Append(r.seconds.ToString("0.###", ci)).Append(',').Append(r.RequestsPerSec.ToString("0.###", ci)).Append(',')
                  .Append(r.SerializationsPerSec.ToString("0.###", ci)).Append(',').Append(r.BytesPerSerialization.ToString("0.#", ci)).Append(',')
                  .Append(r.BytesPerSec.ToString("0.#", ci)).AppendLine();
            }
            return sb.ToString();
        }
    }
}
