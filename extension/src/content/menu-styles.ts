// The menu's stylesheet, injected into its closed shadow root. Every size and color is explicit so page CSS cannot leak in.

export const RING_SIZE = { width: 280, height: 272 };
export const RING_CENTER = { x: 140, y: 146 };
export const RING_RADIUS = 100;
export const BUBBLE_SIZE = 84;

/** Inline on the host element, since page rules can still reach the host itself. */
export const HOST_STYLE =
  "all: initial !important; position: fixed !important; z-index: 2147483647 !important; display: block !important; " +
  "margin: 0 !important; padding: 0 !important; border: 0 !important; pointer-events: none !important;";

export const MENU_CSS = `
:host { all: initial; }
* { box-sizing: border-box; margin: 0; padding: 0; }
.root {
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, Ubuntu, Cantarell, "Noto Sans", "Helvetica Neue", Arial, sans-serif;
  font-size: 13px;
  font-weight: 400;
  line-height: 1.35;
  color: #E6EDF5;
  letter-spacing: normal;
  text-align: left;
  text-transform: none;
  direction: ltr;
  width: ${RING_SIZE.width + 40}px;
  max-height: calc(100vh - 16px);
  overflow-y: auto;
  padding: 14px 20px 4px 20px;
  pointer-events: none;
  background: radial-gradient(circle at 50% ${RING_CENTER.y + 14}px, rgba(15, 45, 91, 0.9) 0px, rgba(15, 45, 91, 0.45) 85px, rgba(15, 45, 91, 0) 158px);
}
.ring { position: relative; width: ${RING_SIZE.width}px; height: ${RING_SIZE.height}px; }
button {
  font: inherit;
  color: inherit;
  cursor: pointer;
  outline: none;
  pointer-events: auto;
  -webkit-tap-highlight-color: transparent;
}
.mark {
  position: absolute;
  left: ${RING_CENTER.x - 34}px;
  top: ${RING_CENTER.y - 34}px;
  width: 68px;
  height: 68px;
  border-radius: 50%;
  border: 2px solid #0F2D5B;
  background: #0F2D5B;
  box-shadow: 0 0 24px 6px rgba(15, 45, 91, 0.8);
  display: flex;
  align-items: center;
  justify-content: center;
}
.mark svg { width: 40px; height: 40px; display: block; }
.bubble {
  position: absolute;
  width: ${BUBBLE_SIZE}px;
  height: ${BUBBLE_SIZE}px;
  border-radius: 50%;
  border: 2px solid #283644;
  background: #141D26;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  padding: 6px;
  transition: border-color 120ms ease;
}
.bubble:hover, .bubble.focused { border-color: #F5A623; }
.bubble svg { display: block; color: #E6EDF5; }
.bubble .title {
  font-size: 11px;
  line-height: 13px;
  font-weight: 500;
  text-align: center;
  color: #E6EDF5;
  max-width: 70px;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.bubble .key {
  position: absolute;
  top: -4px;
  right: -4px;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  border: 1px solid #283644;
  background: #0E131B;
  color: #F5A623;
  font-size: 11px;
  line-height: 18px;
  font-weight: 600;
  text-align: center;
}
.ring.running .bubble { cursor: default; }
.ring.running .bubble:not(.active) > * { opacity: 0.35; }
.ring.running .bubble:hover { border-color: #283644; }
.ring.running .bubble.active, .ring.running .bubble.active:hover { border-color: #F5A623; }
.ring.gone .bubble { cursor: default; }
.ring.gone .bubble > * { opacity: 0.35; }
.ring.gone .bubble:hover { border-color: #283644; }
.spinner {
  position: absolute;
  inset: -6px;
  border-radius: 50%;
  border: 3px solid transparent;
  border-top-color: #F5A623;
  animation: juvin-spin 900ms linear infinite;
  pointer-events: none;
}
@keyframes juvin-spin { to { transform: rotate(360deg); } }
.status {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 6px;
  margin-top: 6px;
}
.chip {
  font-size: 11px;
  line-height: 14px;
  color: #B7C4D3;
  background: #141D26;
  border: 1px solid #283644;
  border-radius: 10px;
  padding: 2px 8px;
  pointer-events: auto;
}
.card {
  margin-top: 10px;
  background: #141D26;
  border: 1px solid #283644;
  border-radius: 10px;
  padding: 12px;
  pointer-events: auto;
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.4);
}
.card .heading { font-size: 13px; line-height: 18px; font-weight: 600; color: #E6EDF5; margin-bottom: 4px; }
.card .message { max-height: 240px; overflow: auto; font-size: 13px; line-height: 18px; color: #E6EDF5; white-space: pre-wrap; overflow-wrap: anywhere; }
.card .result {
  user-select: text;
  margin-top: 8px;
  max-height: 180px;
  overflow: auto;
  font-size: 12px;
  line-height: 17px;
  color: #D5DEE8;
  background: #0E131B;
  border: 1px solid #283644;
  border-radius: 6px;
  padding: 8px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 10px; }
.actions button {
  font-size: 12px;
  line-height: 16px;
  font-weight: 600;
  border-radius: 6px;
  padding: 6px 12px;
  border: 1px solid #283644;
  background: #0E131B;
  color: #E6EDF5;
}
.actions button:hover { border-color: #F5A623; }
.actions button.primary { background: #F5A623; border-color: #F5A623; color: #0E131B; }
.note {
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, Ubuntu, Cantarell, "Noto Sans", "Helvetica Neue", Arial, sans-serif;
  font-size: 12px;
  line-height: 16px;
  font-weight: 500;
  color: #E6EDF5;
  background: #141D26;
  border: 1px solid #283644;
  border-left: 3px solid #F5A623;
  border-radius: 8px;
  padding: 8px 12px;
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.4);
  white-space: nowrap;
  opacity: 1;
  transition: opacity 400ms ease;
}
.note.fading { opacity: 0; }
`;
