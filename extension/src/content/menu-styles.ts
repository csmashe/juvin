// The menu's stylesheet, injected into its closed shadow root. Every size and color is explicit so page CSS cannot leak in.

export const RING_SIZE = { width: 460, height: 310 };
export const RING_CENTER = { x: 230, y: 196 };
export const RING_RADIUS = 150;
export const BUBBLE_SIZE = { width: 140, height: 100 };
const MARK_SIZE = 120;

/** Icon chip colors per action id; any other id gets the neutral chip. */
const CHIP_COLORS: Record<string, { icon: string; background: string }> = {
  clearer: { icon: "#2F6FED", background: "#E8F0FE" },
  soften: { icon: "#7B5CF5", background: "#F0EBFF" },
  shorter: { icon: "#1F9D55", background: "#E6F6EC" },
  missing: { icon: "#F5A623", background: "#FFF3DC" },
  grammar: { icon: "#E5484D", background: "#FDECEC" },
};

const CHIP_CSS = Object.entries(CHIP_COLORS)
  .map(([id, { icon, background }]) => `.bubble[data-action="${id}"] .icon { color: ${icon}; background: ${background}; }`)
  .join("\n");

const FONT = `system-ui, -apple-system, "Segoe UI", Roboto, Ubuntu, Cantarell, "Noto Sans", "Helvetica Neue", Arial, sans-serif`;
const SHADOW = "0 8px 24px rgba(15, 45, 91, 0.12)";
const SHADOW_RAISED = "0 12px 30px rgba(15, 45, 91, 0.2)";
const HAIRLINE = "rgba(15, 45, 91, 0.06)";

/** Inline on the host element, since page rules can still reach the host itself. */
export const HOST_STYLE =
  "all: initial !important; position: fixed !important; z-index: 2147483647 !important; display: block !important; " +
  "margin: 0 !important; padding: 0 !important; border: 0 !important; pointer-events: none !important;";

export const MENU_CSS = `
:host { all: initial; }
* { box-sizing: border-box; margin: 0; padding: 0; }
.root {
  font-family: ${FONT};
  font-size: 15px;
  font-weight: 400;
  line-height: 1.4;
  color: #1F2A37;
  letter-spacing: normal;
  text-align: left;
  text-transform: none;
  direction: ltr;
  width: ${RING_SIZE.width + 40}px;
  max-height: calc(100vh - 16px);
  overflow-x: hidden;
  overflow-y: auto;
  padding: 14px 20px 20px 20px;
  pointer-events: none;
  background: radial-gradient(ellipse 250px 230px at 50% ${RING_CENTER.y + 14}px, rgba(255, 255, 255, 0.92) 0%, rgba(240, 245, 252, 0.7) 55%, rgba(240, 245, 252, 0) 100%);
}
.ring { position: relative; isolation: isolate; width: ${RING_SIZE.width}px; height: ${RING_SIZE.height}px; }
.spokes { position: absolute; left: 0; top: 0; width: ${RING_SIZE.width}px; height: ${RING_SIZE.height}px; overflow: visible; pointer-events: none; }
.spokes line { stroke: rgba(15, 45, 91, 0.12); stroke-width: 1; }
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
  left: ${RING_CENTER.x - MARK_SIZE / 2}px;
  top: ${RING_CENTER.y - MARK_SIZE / 2}px;
  width: ${MARK_SIZE}px;
  height: ${MARK_SIZE}px;
  border-radius: 50%;
  border: 1px solid ${HAIRLINE};
  background: #FFFFFF;
  box-shadow: ${SHADOW};
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: default;
}
.mark::before {
  content: "";
  position: absolute;
  inset: -40px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(255, 255, 255, 0.95) 0%, rgba(255, 255, 255, 0.6) 50%, rgba(255, 255, 255, 0) 72%);
  z-index: -1;
}
.mark svg { width: 72px; height: 72px; display: block; }
.bubble {
  position: absolute;
  width: ${BUBBLE_SIZE.width}px;
  height: ${BUBBLE_SIZE.height}px;
  border-radius: 36px;
  border: 1px solid ${HAIRLINE};
  background: #FFFFFF;
  box-shadow: ${SHADOW};
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  padding: 6px 12px;
  transition: transform 120ms ease, box-shadow 120ms ease;
}
.bubble:hover, .bubble.focused { transform: translateY(-2px); box-shadow: ${SHADOW_RAISED}, 0 0 0 2px #0F2D5B; }
.bubble .icon {
  position: relative;
  flex: none;
  width: 44px;
  height: 44px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #6B7A90;
  background: #F1F4F8;
}
${CHIP_CSS}
.bubble .icon svg { display: block; }
.bubble .title {
  font-size: 16px;
  line-height: 19px;
  font-weight: 600;
  text-align: center;
  color: #1F2A37;
  max-width: 116px;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.ring.running .bubble, .ring.gone .bubble { cursor: default; }
.ring.running .bubble:hover, .ring.gone .bubble:hover { transform: none; box-shadow: ${SHADOW}; }
/* Only the contents fade, so the spokes stay hidden behind the white card. */
.ring.running .bubble:not(.active) > *, .ring.gone .bubble > * { opacity: 0.45; }
.spinner {
  position: absolute;
  inset: -5px;
  border-radius: 50%;
  border: 2px solid transparent;
  border-top-color: #0F2D5B;
  border-right-color: #0F2D5B;
  animation: juvin-spin 900ms linear infinite;
  pointer-events: none;
}
@keyframes juvin-spin { to { transform: rotate(360deg); } }
.card {
  position: relative;
  margin-top: 22px;
  background: #FFFFFF;
  border: 1px solid ${HAIRLINE};
  border-radius: 20px;
  padding: 24px;
  pointer-events: auto;
  box-shadow: ${SHADOW};
}
.card::before {
  content: "";
  position: absolute;
  left: 50%;
  top: -9px;
  width: 16px;
  height: 16px;
  margin-left: -8px;
  background: #FFFFFF;
  border-top: 1px solid rgba(15, 45, 91, 0.08);
  border-left: 1px solid rgba(15, 45, 91, 0.08);
  border-top-left-radius: 3px;
  transform: rotate(45deg);
}
.card .close {
  position: absolute;
  top: 18px;
  right: 18px;
  width: 32px;
  height: 32px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: #6B7A90;
  display: flex;
  align-items: center;
  justify-content: center;
}
.card .close:hover { background: #F1F4F8; color: #1F2A37; }
.card .close svg { width: 24px; height: 24px; display: block; }
.card .close + * { padding-right: 36px; }
.card .heading { font-size: 22px; line-height: 28px; font-weight: 600; color: #1F2A37; margin-bottom: 8px; }
.card .message { max-height: 240px; overflow: auto; font-size: 15px; line-height: 22px; color: #3B4858; white-space: pre-wrap; overflow-wrap: anywhere; }
.card .result {
  user-select: text;
  margin-top: 14px;
  max-height: 200px;
  overflow: auto;
  font-size: 14px;
  line-height: 21px;
  color: #1F2A37;
  background: #F6F8FB;
  border: 1px solid rgba(15, 45, 91, 0.08);
  border-radius: 12px;
  padding: 12px 14px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.actions { display: flex; justify-content: flex-end; gap: 10px; margin-top: 18px; }
.actions button {
  font-size: 16px;
  line-height: 20px;
  font-weight: 600;
  border-radius: 10px;
  padding: 12px 24px;
  border: 1px solid rgba(15, 45, 91, 0.14);
  background: #FFFFFF;
  color: #1F2A37;
}
.actions button:hover { background: #F6F8FB; }
.actions button.primary { background: #0F2D5B; border-color: #0F2D5B; color: #FFFFFF; }
.actions button.primary:hover { background: #163C75; }
.note {
  font-family: ${FONT};
  font-size: 13px;
  line-height: 18px;
  font-weight: 500;
  color: #1F2A37;
  background: #FFFFFF;
  border: 1px solid ${HAIRLINE};
  border-left: 3px solid #F5A623;
  border-radius: 10px;
  padding: 10px 14px;
  box-shadow: ${SHADOW};
  white-space: nowrap;
  opacity: 1;
  transition: opacity 400ms ease;
}
.note.fading { opacity: 0; }
`;
