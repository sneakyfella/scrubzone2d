using ScrubZone2D.Network;

namespace ScrubZone2D.Gameplay;

public sealed class ObjectiveManager
{
    private readonly GameMode _mode;

    public int Score0 { get; private set; }
    public int Score1 { get; private set; }

    private int ScoreLimit => _mode == GameMode.FFA ? 10 : 15;

    public ObjectiveManager(GameMode mode) => _mode = mode;

    // Call on the killing side; returns the packet to broadcast
    public ScoreUpdatePacket RecordKill(bool killerIsPlayer0)
    {
        if (killerIsPlayer0) Score0++;
        else                 Score1++;
        return new ScoreUpdatePacket { Score0 = (byte)Score0, Score1 = (byte)Score1 };
    }

    // Call on the receiving side to sync authoritative scores
    public void ApplyScore(ScoreUpdatePacket pkt)
    {
        Score0 = pkt.Score0;
        Score1 = pkt.Score1;
    }

    public bool IsGameOver() => Score0 >= ScoreLimit || Score1 >= ScoreLimit;

    public string GetResultText(bool localIsPlayer0)
    {
        bool player0Won = Score0 >= ScoreLimit;
        bool localWon   = localIsPlayer0 ? player0Won : !player0Won;
        int  myScore    = localIsPlayer0 ? Score0 : Score1;
        int  theirScore = localIsPlayer0 ? Score1 : Score0;
        return localWon
            ? $"You Win!  ({myScore} - {theirScore})"
            : $"You Lose! ({myScore} - {theirScore})";
    }
}
