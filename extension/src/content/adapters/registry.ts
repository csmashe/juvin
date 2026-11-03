import type { PageLocation, SiteAdapter } from "./adapter";
import { genericAdapter } from "./generic";
import { gmailAdapter } from "./gmail";
import { outlookAdapter } from "./outlook";
import { testPageAdapter } from "./test-page";

/** Most specific first; the generic adapter is last because it matches every page. */
export const ADAPTERS: readonly SiteAdapter[] = [testPageAdapter, gmailAdapter, outlookAdapter, genericAdapter];

export function selectAdapter(location: PageLocation, adapters: readonly SiteAdapter[] = ADAPTERS): SiteAdapter | undefined {
  return adapters.find((adapter) => adapter.matches(location));
}
