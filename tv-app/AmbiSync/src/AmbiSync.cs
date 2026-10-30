using System;

namespace TizenAmbilight
{
    /// <summary>AmbiSync-Ergänzungen der Hauptschleife.</summary>
    static partial class Ambilight
    {
        static ZoneColors _uiRaw;
        static ZoneColors _blendBuf;

        /// <summary>
        /// Kein Videobild verfügbar (Startseite, Einstellungen, App-Menü ohne laufendes Video):
        /// statt den Streifen auszuschalten, die Zonenfarben der UI-Ebene verwenden.
        /// Liefert false, wenn keine aktuelle UI-Aufnahme vorliegt - dann verhält sich die Schleife
        /// wie das Original (Pause, später Blackout).
        /// </summary>
        static bool TryUiOnly(Config cfg, Stats st, ref ZoneLayout layout, ref ZoneColors smoothed, out ZoneColors raw, bool protectedVideo)
        {
            raw = null;
            if (!UiOverlay.HasFresh(cfg.ZoneCount)) return false;
            if (_uiRaw == null || _uiRaw.Count != cfg.ZoneCount) _uiRaw = new ZoneColors(cfg.ZoneCount);
            if (!UiOverlay.CopyUiOnly(_uiRaw, protectedVideo)) return false;

            if (layout == null || layout.Total != cfg.ZoneCount)
            {
                // gleiche Zonenaufteilung wie die Video-Ebene, damit das LED-Mapping identisch bleibt
                layout = new ZoneLayout(VideoPlaneCapture.ReqW, VideoPlaneCapture.ReqH, cfg.ZoneCount, cfg.BandFrac);
                smoothed = null;
                st.LastDims = VideoPlaneCapture.ReqW + "x" + VideoPlaneCapture.ReqH;
                L("zone layout (ui): " + layout.Describe());
            }

            st.BackendActive = "ui_only";
            st.LastRetPath = "(ui layer)";
            st.CaptureOk++;
            st.ConsecutiveFails = 0;
            st.BackoffStep = 0;
            st.UiOnlyFrames++;
            NoteGoodFrame();
            NoteContentPpi(_uiRaw, st);
            // Ein stehendes Menü (Startseite) soll nicht wie ein angehaltener Film abgedunkelt werden -
            // außer während eines geschützten Films, dort ist eine stehende UI nur ein Rest.
            if (!protectedVideo && UiOverlay.AppliedCoverage > 0.2) st.LastContentChangeUtc = DateTime.UtcNow;
            raw = _uiRaw;
            return true;
        }
    }
}
