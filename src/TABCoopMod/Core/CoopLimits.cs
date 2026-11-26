using System.Drawing;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Central co-op player limits and shared player color palette.
    /// Change MaxPlayers here to resize the lobby, transports and player-color table in one place.
    /// </summary>
    public static class CoopLimits
    {
        // Self-use build: Archon mode supports up to 6 players by default.
        // Lobby layout / colors are tuned for 4..6; 7+ may need additional UI layout work.
        public const int MaxPlayers = 6;

        // Multi-Faction mode remains capped at 3 players; the extra economy / map generation assumes 2..3.
        public const int MaxFactionPlayers = 3;

        // Currently only 2P / 3P balance profiles exist.
        public const int MaxBalanceEnhancementPlayers = 3;

        public static readonly Color[] PlayerColors =
        {
            Color.DodgerBlue,   // P1
            Color.SpringGreen,  // P2
            Color.Orange,       // P3
            Color.MediumPurple, // P4
            Color.Gold,         // P5
            Color.DeepPink      // P6
        };

        public static Color GetPlayerColor(int playerId)
        {
            if (playerId < 1 || PlayerColors.Length == 0) return Color.White;
            return PlayerColors[(playerId - 1) % PlayerColors.Length];
        }
    }
}
