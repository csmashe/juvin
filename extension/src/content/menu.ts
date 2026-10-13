// Placeholder until the in-page renderer lands: declines every menu, so the app opens its rewrite panel instead.

import type { MenuUpdateMessage } from "../shared/protocol";
import type { MenuShowRequest, MenuShowResult } from "../shared/runtime-messages";

export class MenuRenderer {
  show(_request: MenuShowRequest): MenuShowResult {
    return { ok: false, error: { code: "unsupported", message: "This extension cannot draw the menu yet." } };
  }

  update(_update: MenuUpdateMessage): void {}

  close(_menuId: string): void {}
}
