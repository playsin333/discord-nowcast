using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // Discord's local IPC pipe, \\.\pipe\discord-ipc-N, the one games use.
    // A frame is the opcode (int32), the length (int32) and JSON in UTF-8.
    class Discord : IDisposable
    {
        const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        NamedPipeClientStream pipe;
        // Why Discord was not reached last time: written to the log once, not every 3 s.
        string lastMiss;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, int size, IntPtr read, out int available, IntPtr left);

        public bool IsConnected
        {
            get { return pipe != null; }
        }

        // The application the connection speaks for.
        public string ClientId { get; private set; }

        // False once Discord has closed its end: it quit or restarted. Without
        // this a restart goes unnoticed until the track changes, since only a
        // write would fail.
        public bool IsAlive
        {
            get
            {
                int available;
                return pipe != null && PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero);
            }
        }

        public bool Connect(string clientId)
        {
            // Only the pipes that exist: waiting on absent ones would hold up
            // the whole tick every few seconds while Discord is closed.
            string[] pipes;
            try
            {
                pipes = Directory.GetFiles(@"\\.\pipe\");
            }
            catch (Exception)
            {
                pipes = new string[0];
            }
            var miss = "no discord-ipc pipe: Discord is closed, or it is the browser version";
            for (var index = 0; index < 10; index++)
            {
                var name = "discord-ipc-" + index;
                if (!pipes.Any(p => p.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase))) continue;
                var candidate = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    candidate.Connect(300);
                }
                catch (Exception e)
                {
                    // Access denied: Discord runs as administrator and this does not.
                    miss = name + ": " + e.Message;
                    candidate.Dispose();
                    continue;
                }
                try
                {
                    Send(candidate, Handshake, new Dictionary<string, object> { { "v", 1 }, { "client_id", clientId } });
                    var reply = Read(candidate);
                    if (Value(reply, "evt") == "READY")
                    {
                        pipe = candidate;
                        ClientId = clientId;
                        var data = reply["data"] as Dictionary<string, object>;
                        var user = data == null ? null : data["user"] as Dictionary<string, object>;
                        Log.Write("Connected to Discord" + (user == null ? "" : " as " + Value(user, "username")));
                        lastMiss = null;
                        return true;
                    }
                    Log.Write("Discord refused: " + json.Serialize(reply));
                }
                catch (Exception e)
                {
                    Log.Write("Discord refused: " + e.Message);
                }
                candidate.Dispose();
                miss = null;
            }
            if (miss != null && miss != lastMiss) Log.Write("Discord not found: " + miss);
            lastMiss = miss;
            return false;
        }

        // null clears the status. Returns Discord's complaint, or null when it took it.
        public string SetActivity(Dictionary<string, object> activity)
        {
            var arguments = new Dictionary<string, object> { { "pid", Process.GetCurrentProcess().Id } };
            if (activity != null) arguments["activity"] = activity;
            Send(pipe, Frame, new Dictionary<string, object>
            {
                { "cmd", "SET_ACTIVITY" },
                { "args", arguments },
                { "nonce", Guid.NewGuid().ToString() },
            });
            var reply = Read(pipe);
            if (Value(reply, "evt") == "ERROR")
            {
                var data = reply["data"] as Dictionary<string, object>;
                var message = data == null ? "" : Value(data, "message");
                Log.Write("Discord did not take the status: " + message);
                return message;
            }
            return null;
        }

        public void Dispose()
        {
            if (pipe != null)
            {
                try
                {
                    pipe.Dispose();
                }
                catch (Exception)
                {
                }
                pipe = null;
                ClientId = null;
            }
        }

        void Send(Stream stream, int op, object body)
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(body));
            var frame = new byte[8 + bytes.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(bytes.Length).CopyTo(frame, 4);
            bytes.CopyTo(frame, 8);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        Dictionary<string, object> Read(Stream stream)
        {
            while (true)
            {
                var header = ReadExactly(stream, 8);
                var op = BitConverter.ToInt32(header, 0);
                var length = BitConverter.ToInt32(header, 4);
                var text = length > 0 ? Encoding.UTF8.GetString(ReadExactly(stream, length)) : "{}";
                var data = json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
                if (op == Ping)
                {
                    Send(stream, Pong, data);
                    continue;
                }
                if (op == Close) throw new IOException("Discord closed the connection: " + Value(data, "message"));
                return data;
            }
        }

        static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            var got = 0;
            while (got < count)
            {
                var task = stream.ReadAsync(buffer, got, count - got);
                if (!task.Wait(10000)) throw new IOException("Discord does not answer");
                if (task.Result <= 0) throw new IOException("Discord closed the connection");
                got += task.Result;
            }
            return buffer;
        }

        static string Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }
    }
}
