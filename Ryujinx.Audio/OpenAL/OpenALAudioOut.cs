using OpenTK.Audio;
using OpenTK.Audio.OpenAL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Audio.OpenAL
{
    public class OpenALAudioOut : IAalOutput, IDisposable
    {
        private const int MaxTracks = 256;

        private const int MaxReleased = 32;

        private AudioContext Context;

        private class Track : IDisposable
        {
            public int SourceId { get; private set; }

            public int SampleRate { get; private set; }

            public ALFormat Format { get; private set; }

            private ReleaseCallback Callback;

            public PlaybackState State { get; set; }

            private bool ShouldCallReleaseCallback;

            private ConcurrentDictionary<long, int> Buffers;

            private Queue<long> QueuedTagsQueue;

            private Queue<long> ReleasedTagsQueue;

            private int LastReleasedCount;

            private bool Disposed;

            public Track(int SampleRate, ALFormat Format, ReleaseCallback Callback)
            {
                this.SampleRate = SampleRate;
                this.Format     = Format;
                this.Callback   = Callback;

                State = PlaybackState.Stopped;

                SourceId = AL.GenSource();

                Buffers = new ConcurrentDictionary<long, int>();

                QueuedTagsQueue = new Queue<long>();

                ReleasedTagsQueue = new Queue<long>();
            }

            public bool ContainsBuffer(long Tag)
            {
                SyncQueuedTags();

                foreach (long QueuedTag in QueuedTagsQueue)
                {
                    if (QueuedTag == Tag)
                    {
                        return true;
                    }
                }

                return false;
            }

            public long[] GetReleasedBuffers(int MaxCount)
            {
                ClearReleased();

                List<long> Tags = new List<long>();

                HashSet<long> Unique = new HashSet<long>();

                while (MaxCount-- > 0 && ReleasedTagsQueue.TryDequeue(out long Tag))
                {
                    if (Unique.Add(Tag))
                    {
                        Tags.Add(Tag);
                    }
                }

                return Tags.ToArray();
            }

            public int AppendBuffer(long Tag)
            {
                if (Disposed)
                {
                    throw new ObjectDisposedException(nameof(Track));
                }

                int Id = AL.GenBuffer();

                Buffers.AddOrUpdate(Tag, Id, (Key, OldId) =>
                {
                    AL.DeleteBuffer(OldId);

                    return Id;
                });

                QueuedTagsQueue.Enqueue(Tag);

                return Id;
            }

            public void ClearReleased()
            {
                SyncQueuedTags();

                AL.GetSource(SourceId, ALGetSourcei.BuffersProcessed, out int ReleasedCount);

                CheckReleaseChanges(ReleasedCount);

                if (ReleasedCount > 0)
                {
                    AL.SourceUnqueueBuffers(SourceId, ReleasedCount);
                }
            }

            public void CallReleaseCallbackIfNeeded()
            {
                CheckReleaseChanges();

                if (ShouldCallReleaseCallback)
                {
                    ShouldCallReleaseCallback = false;

                    Callback();
                }
            }

            private void CheckReleaseChanges()
            {
                AL.GetSource(SourceId, ALGetSourcei.BuffersProcessed, out int ReleasedCount);

                CheckReleaseChanges(ReleasedCount);
            }

            private void CheckReleaseChanges(int NewReleasedCount)
            {
                if (LastReleasedCount != NewReleasedCount)
                {
                    LastReleasedCount = NewReleasedCount;

                    ShouldCallReleaseCallback = true;
                }
            }

            private void SyncQueuedTags()
            {
                AL.GetSource(SourceId, ALGetSourcei.BuffersQueued,    out int QueuedCount);
                AL.GetSource(SourceId, ALGetSourcei.BuffersProcessed, out int ReleasedCount);

                QueuedCount -= ReleasedCount;

                while (QueuedTagsQueue.Count > QueuedCount)
                {
                    ReleasedTagsQueue.Enqueue(QueuedTagsQueue.Dequeue());
                }

                while (ReleasedTagsQueue.Count > MaxReleased)
                {
                    ReleasedTagsQueue.Dequeue();
                }
            }

            public void Dispose()
            {
                Dispose(true);
            }

            protected virtual void Dispose(bool Disposing)
            {
                if (Disposing && !Disposed)
                {
                    Disposed = true;

                    AL.DeleteSource(SourceId);

                    foreach (int Id in Buffers.Values)
                    {
                        AL.DeleteBuffer(Id);
                    }
                }
            }
        }

        private ConcurrentDictionary<int, Track> Tracks;

        private Thread AudioPollerThread;

        private bool KeepPolling;

        public OpenALAudioOut()
        {
            Context = new AudioContext();

            Tracks = new ConcurrentDictionary<int, Track>();

            KeepPolling = true;

            AudioPollerThread = new Thread(AudioPollerWork);

            AudioPollerThread.Start();
        }

        private void AudioPollerWork()
        {
            do
            {
                foreach (Track Td in Tracks.Values)
                {
                    Td.CallReleaseCallbackIfNeeded();
                }

                //If it's not slept it will waste cycles.
                Thread.Sleep(10);
            }
            while (KeepPolling);

            foreach (Track Td in Tracks.Values)
            {
                Td.Dispose();
            }

            Tracks.Clear();
        }

        public int OpenTrack(int SampleRate, int Channels, ReleaseCallback Callback)
        {
            Track Td = new Track(SampleRate, GetALFormat(Channels), Callback);

            for (int Id = 0; Id < MaxTracks; Id++)
            {
                if (Tracks.TryAdd(Id, Td))
                {
                    return Id;
                }
            }

            return -1;
        }

        private ALFormat GetALFormat(int Channels)
        {
            switch (Channels)
            {
                case 1: return ALFormat.Mono16;
                case 2: return ALFormat.Stereo16;
                case 6: return ALFormat.Multi51Chn16Ext;
            }

            throw new ArgumentOutOfRangeException(nameof(Channels));
        }

        public void CloseTrack(int Track)
        {
            if (Tracks.TryRemove(Track, out Track Td))
            {
                Td.Dispose();
            }
        }

        public bool ContainsBuffer(int Track, long Tag)
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                return Td.ContainsBuffer(Tag);
            }

            return false;
        }

        public long[] GetReleasedBuffers(int Track, int MaxCount)
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                return Td.GetReleasedBuffers(MaxCount);
            }

            return null;
        }

        public void AppendBuffer<T>(int Track, long Tag, T[] Buffer) where T : struct
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                int BufferId = Td.AppendBuffer(Tag);

                int Size = Buffer.Length * Marshal.SizeOf<T>();

                AL.BufferData<T>(BufferId, Td.Format, Buffer, Size, Td.SampleRate);

                AL.SourceQueueBuffer(Td.SourceId, BufferId);

                StartPlaybackIfNeeded(Td);
            }
        }

        public void Start(int Track)
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                Td.State = PlaybackState.Playing;

                StartPlaybackIfNeeded(Td);
            }
        }

        private void StartPlaybackIfNeeded(Track Td)
        {
            AL.GetSource(Td.SourceId, ALGetSourcei.SourceState, out int StateInt);

            ALSourceState State = (ALSourceState)StateInt;

            if (State != ALSourceState.Playing && Td.State == PlaybackState.Playing)
            {
                Td.ClearReleased();

                AL.SourcePlay(Td.SourceId);
            }
        }

        public void Stop(int Track)
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                Td.State = PlaybackState.Stopped;

                AL.SourceStop(Td.SourceId);
            }
        }

        public PlaybackState GetState(int Track)
        {
            if (Tracks.TryGetValue(Track, out Track Td))
            {
                return Td.State;
            }

            return PlaybackState.Stopped;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool Disposing)
        {
            if (Disposing)
            {
                KeepPolling = false;
            }
        }
    }
}