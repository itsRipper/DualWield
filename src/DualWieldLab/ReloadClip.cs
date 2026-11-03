using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DualWieldLab;

// Baked Blender choreography (assets/exports/unity/*.json, embedded in the DLL).
// Channels are already in Unity look/weapon spaces; see assets/tools/export/export_clips.py.
internal sealed class ReloadClip
{
    internal sealed class Segment
    {
        internal string Name = "";
        internal float Start, End;
        internal string[] Native = Array.Empty<string>(), Exclude = Array.Empty<string>();
    }

    internal enum SoundSide { Right, Left }
    internal enum SoundGate { None, Swap, Chamber }

    // Marker "snd:<R|L>:<native event>[@swap|@chamber]" authored in Blender.
    internal readonly struct SoundEvent
    {
        internal readonly float Frame;
        internal readonly SoundSide Side;
        internal readonly string Name;
        internal readonly SoundGate Gate;
        internal SoundEvent(float frame, SoundSide side, string name, SoundGate gate)
        { Frame = frame; Side = side; Name = name; Gate = gate; }
    }

    private readonly Dictionary<string, float[]> _channels;
    internal SoundEvent[] Sounds { get; }
    internal string Name { get; }
    internal float Fps { get; }
    internal int Frames { get; }
    internal float LastFrame => Frames - 1;
    internal IReadOnlyDictionary<string, float> Markers { get; }
    internal IReadOnlyDictionary<string, float> Constants { get; }
    internal Segment[] Segments { get; }

    private ReloadClip(JObject json)
    {
        Name = (string?)json["name"] ?? throw new InvalidDataException("Clip has no name.");
        Fps = (float?)json["fps"] ?? 30f;
        Frames = (int?)json["frames"] ?? 0;
        int start = (int?)json["start"] ?? 0;
        if (Frames < 2 || Fps <= 0f) throw new InvalidDataException(Name + " has no frames.");
        Markers = ((JObject?)json["markers"] ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (float)p.Value - start);
        Constants = ((JObject?)json["constants"] ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (float)p.Value);
        Segments = ((JArray?)json["segments"] ?? new JArray()).Select(s => new Segment
        {
            Name = (string)s["name"]!, Start = (float)s["start"]! - start, End = (float)s["end"]! - start,
            Native = s["native"]?.Select(v => (string)v!).ToArray() ?? Array.Empty<string>(),
            Exclude = s["exclude"]?.Select(v => (string)v!).ToArray() ?? Array.Empty<string>(),
        }).ToArray();
        Sounds = Markers.Where(m => m.Key.StartsWith("snd:", StringComparison.Ordinal))
            .Select(m => ParseSound(m.Key, m.Value)).OrderBy(s => s.Frame).ToArray();
        _channels = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var channel in ((JObject?)json["channels"] ?? new JObject()).Properties())
        {
            float[] values = channel.Value.Select(v => (float)v).ToArray();
            if (values.Length % Frames != 0 || values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException(Name + " channel " + channel.Name + " is malformed.");
            _channels.Add(channel.Name, values);
        }
    }

    internal static ReloadClip Parse(string text) => new ReloadClip(JObject.Parse(text));

    private static SoundEvent ParseSound(string marker, float frame)
    {
        string[] parts = marker.Split(':');
        if (parts.Length != 3 || (parts[1] != "R" && parts[1] != "L"))
            throw new InvalidDataException("Malformed sound marker " + marker);
        string[] name = parts[2].Split('@');
        SoundGate gate = name.Length == 1 ? SoundGate.None
            : name[1] == "swap" ? SoundGate.Swap : name[1] == "chamber" ? SoundGate.Chamber
            : throw new InvalidDataException("Unknown sound gate " + marker);
        return new SoundEvent(frame, parts[1] == "R" ? SoundSide.Right : SoundSide.Left, name[0], gate);
    }

    internal static ReloadClip LoadEmbedded(string name)
    {
        using Stream? stream = typeof(ReloadClip).Assembly.GetManifestResourceStream("DualWieldLab.Clips." + name + ".json");
        if (stream == null) throw new InvalidDataException("Embedded clip missing: " + name);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal float Marker(string name) => Markers.TryGetValue(name, out float f) ? f : throw new KeyNotFoundException(Name + " lacks marker " + name);
    internal float Constant(string name, float fallback) => Constants.TryGetValue(name, out float v) ? v : fallback;

    private float[] Channel(string name, int width)
    {
        if (!_channels.TryGetValue(name, out float[] values) || values.Length != Frames * width)
            throw new KeyNotFoundException(Name + " lacks channel " + name);
        return values;
    }

    private void Span(float frame, out int a, out int b, out float t)
    {
        frame = Mathf.Clamp(frame, 0f, LastFrame);
        a = Mathf.FloorToInt(frame);
        b = Math.Min(a + 1, Frames - 1);
        t = frame - a;
    }

    internal float Scalar(string name, float frame)
    {
        float[] v = Channel(name, 1);
        Span(frame, out int a, out int b, out float t);
        return Mathf.Lerp(v[a], v[b], t);
    }

    // Step channels (visibility) hold the earlier key.
    internal float Step(string name, float frame)
    {
        float[] v = Channel(name, 1);
        return v[Mathf.Clamp(Mathf.FloorToInt(frame), 0, Frames - 1)];
    }

    internal Vector3 Vector(string name, float frame)
    {
        float[] v = Channel(name, 3);
        Span(frame, out int a, out int b, out float t);
        return Vector3.Lerp(new Vector3(v[a * 3], v[a * 3 + 1], v[a * 3 + 2]), new Vector3(v[b * 3], v[b * 3 + 1], v[b * 3 + 2]), t);
    }

    internal Quaternion Rotation(string name, float frame)
    {
        float[] v = Channel(name, 4);
        Span(frame, out int a, out int b, out float t);
        var qa = new Quaternion(v[a * 4], v[a * 4 + 1], v[a * 4 + 2], v[a * 4 + 3]);
        var qb = new Quaternion(v[b * 4], v[b * 4 + 1], v[b * 4 + 2], v[b * 4 + 3]);
        return Quaternion.Slerp(qa, qb, t);
    }
}
