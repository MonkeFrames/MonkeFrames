using GorillaNetworking;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class RoomManager : IEditorWindow
{
    public string Name => "Room Manager";
    public Rect Rect => new Rect(60, 60, 340, 150);

    private string roomCode = "";

    public void JoinRoom(string room)
    {
        if (NetworkSystem.Instance.InRoom)
            NetworkSystem.Instance.ReturnToSinglePlayer();

        if (room == "") return;

        PhotonNetworkController.Instance.AttemptToJoinSpecificRoom(room.ToUpper(), JoinType.Solo);
    }

    public void OnDraw()
    {
        float w = Rect.width;
        bool inRoom = NetworkSystem.Instance != null && NetworkSystem.Instance.InRoom;

        // Connection status pill with a pulsing dot while connected.
        float pulse = inRoom ? 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 3f) : 1f;
        Theme.Dot(new Vector2(24, 50), 4.5f, (inRoom ? Theme.AxisY : Theme.TextMuted).WithAlpha(pulse));
        GUI.Label(new Rect(34, 38, w - 50, 24), inRoom ? "Connected to a room" : "Not in a room", Theme.Muted);

        GUI.Label(new Rect(16, 68, 80, 26), "Room code");
        roomCode = GUI.TextField(new Rect(96, 68, w - 112, 26), roomCode ?? "").ToUpper();

        if (GUI.Button(new Rect(16, 104, (w - 40) / 2f, 28), "Join", Theme.AccentButton))
            JoinRoom(roomCode);

        if (GUI.Button(new Rect(24 + (w - 40) / 2f, 104, (w - 40) / 2f, 28), "Disconnect", Theme.DangerButton))
            JoinRoom("");
    }
}
