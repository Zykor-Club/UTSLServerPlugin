// 仅用于「离线类型检查」的桩：签名与真实 Terraria / R.PluginSwitch 对齐（参考反编译结果）。
// 不参与分发；真机构建请用 RuYouMusicClient.csproj（引用游戏目录里的真实程序集）。
using System;
using System.IO;

namespace Terraria
{
    public class Player { public bool musicBoxSilence; }
    public class MessageBuffer { public BinaryReader reader; }
    public class NetplayConnection { public Terraria.Net.Sockets.ISocket Socket; public string StatusText; }
    public static class Netplay { public static NetplayConnection Connection; }
    public static class NetMessage { public static MessageBuffer[] buffer = new MessageBuffer[257]; }
    public static class Main
    {
        public static Player LocalPlayer;
        public static float musicVolume = 1f;
        public static bool gameMenu;
        public static string statusText;
        public static void NewText(string text, byte r, byte g, byte b) { }
    }
}

namespace Terraria.Net.Sockets
{
    public delegate void SocketSendCallback(object state);
    public interface ISocket { void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state); }
}

namespace R
{
    [AttributeUsage(AttributeTargets.Class)]
    public class PluginAttribute : Attribute
    {
        public PluginAttribute(int id, string name, string author, string desc) { }
        public PluginAttribute(int id, string name, string author, string desc, int version) { }
    }
}

namespace R.PluginSwitch.Hooks
{
    public delegate void Hook();
    public delegate void Hook<T>(T t) where T : EventArgs;
    public class HandledEventArgs : EventArgs { public bool Handled; }
    public class HGetData : HandledEventArgs { public int Start; public int Length; public int MSGType; }
    public class HandlerCollection { public void Register(Hook h) { } }
    public class HandlerCollection<T> where T : HandledEventArgs { public void Register(Hook<T> h) { } }
    public static class GameHooks
    {
        public static readonly HandlerCollection<HGetData> NetGetData = new HandlerCollection<HGetData>();
        public static readonly HandlerCollection PlayerUpdate = new HandlerCollection();
        public static readonly HandlerCollection<HandledEventArgs> PreUpdate = new HandlerCollection<HandledEventArgs>();
    }
}
