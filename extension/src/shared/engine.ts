// Set by build.mjs: true in the Firefox build, which only runs in Gecko. Undefined in tests.
declare const JUVIN_GECKO: boolean | undefined;

export const inGecko: boolean = typeof JUVIN_GECKO !== "undefined" && JUVIN_GECKO;
