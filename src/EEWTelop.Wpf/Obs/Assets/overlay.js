"use strict";

const token = new URLSearchParams(window.location.search).get("token") ?? "";
const view = window.location.pathname.startsWith("/eew")
  ? "eew"
  : window.location.pathname.startsWith("/tsunami")
    ? "tsunami"
    : window.location.pathname.startsWith("/weather")
      ? "weather"
      : "general";
const canvas = document.getElementById("canvas");
const banner = document.getElementById("banner");
const content = document.getElementById("content");
const blocks = document.getElementById("blocks");
const pageIndicator = document.getElementById("pageIndicator");
const alertAudio = document.getElementById("alertAudio");
// Each OBS source receives only its own category's audio commands. The browser
// preview remains silent and does not subscribe as an audio client.
const monitorMode = window.location.pathname === "/monitor/view";
const handlesAudio = !monitorMode;
let lastAudioSequence = 0;
let activeAudioSequence = 0;
const lastReportedAudioResults = new Map();
const maximumAudioCommandAgeMilliseconds = 10000;
let outputTransform = {
  scale: 1, offsetX: 0, offsetY: 0,
  cropLeft: 0, cropTop: 0, cropRight: 0, cropBottom: 0
};
const allowedStyles = new Set([
  "correction", "eew-header", "eew-header-cancel", "eew-header-test",
  "eew-warning", "eew-areas", "summary", "advisory",
  "intensity", "comment", "tsunami", "tsunami-cancel", "weather-advisory",
  "weather-warning", "weather-danger-warning", "weather-special-warning", "weather-cancel",
  "volcano-forecast", "volcano-warning", "eruption-flash", "empty"
]);

function resizeCanvas() {
  const fit = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
  const zoom = Math.min(4, Math.max(.25, Number(outputTransform.scale) || 1));
  canvas.style.transformOrigin = "960px 540px";
  canvas.style.transform = `scale(${fit * zoom})`;
  canvas.style.left = `${(window.innerWidth - 1920) / 2 + outputTransform.offsetX * fit}px`;
  canvas.style.top = `${(window.innerHeight - 1080) / 2 + outputTransform.offsetY * fit}px`;
  canvas.style.clipPath = `inset(${outputTransform.cropTop}px ${outputTransform.cropRight}px ${outputTransform.cropBottom}px ${outputTransform.cropLeft}px)`;
}

function applyOutputTransform(value) {
  const transform = value ?? {};
  outputTransform = {
    scale: Number(transform.scale) || 1,
    offsetX: Number(transform.offsetX) || 0,
    offsetY: Number(transform.offsetY) || 0,
    cropLeft: Math.max(0, Number(transform.cropLeft) || 0),
    cropTop: Math.max(0, Number(transform.cropTop) || 0),
    cropRight: Math.max(0, Number(transform.cropRight) || 0),
    cropBottom: Math.max(0, Number(transform.cropBottom) || 0)
  };
  resizeCanvas();
}

function textNode(className, value) {
  const node = document.createElement("div");
  node.className = className;
  node.textContent = value ?? "";
  return node;
}

const badgeColors = new Map([
  ["長周期階級1", ["#075cff", "#fff"]], ["長周期階級2", ["#ffe600", "#222"]],
  ["長周期階級3", ["#f04416", "#fff"]], ["長周期階級4", ["#a00032", "#fff"]],
  ["震度1", ["#3b8fd4", "#fff"]], ["震度2", ["#3fb85f", "#fff"]],
  ["震度3", ["#ffe000", "#222"]], ["震度4", ["#ffb000", "#fff"]],
  ["震度5弱", ["#ff7a1a", "#fff"]], ["震度5弱以上", ["#ff5500", "#fff"]],
  ["震度5弱以上 未入電", ["#ff5500", "#fff"]],
  ["震度5強", ["#ff8000", "#fff"]], ["震度6弱", ["#ff3b1f", "#fff"]],
  ["震度6強", ["#d0004a", "#fff"]], ["震度7", ["#a000a0", "#fff"]],
  ["大津波警報", ["#c00060", "#fff"]], ["津波警報", ["#ff2d1a", "#fff"]],
  ["津波注意報", ["#ffd000", "#222"]], ["訂正", ["#d35400", "#fff"]]
]);

function renderBlock(item, previousBadge) {
  const block = document.createElement("div");
  const style = allowedStyles.has(item.styleToken) ? item.styleToken : "summary";
  block.className = `block ${style}`;
  const weatherStyle = style.startsWith("weather-");
  const contextEnd = (item.primaryText || "").indexOf("\n");
  const explicitHeading = item.weatherHeading || "";
  const hasWeatherContext = weatherStyle && item.badge && (explicitHeading || contextEnd > 0 &&
    item.primaryText.slice(0, contextEnd).includes("｜"));
  if (hasWeatherContext) block.classList.add("weather-area-heading");
  const volcanoStyle = style.startsWith("volcano-") || style === "eruption-flash";
  const reservesBadge = (style === "intensity" || style === "tsunami" || style === "correction" || weatherStyle || volcanoStyle) &&
    (item.badge || previousBadge);
  if (reservesBadge && !item.hideBadge) {
    const badgeText = item.badge || previousBadge;
    const badge = textNode(`badge${item.badge ? "" : " placeholder"}`, badgeText);
    const colors = badgeColors.get(badgeText) ?? (style === "weather-special-warning"
      ? ["#08050a", "#fff"]
      : style === "weather-danger-warning"
        ? ["#8f1aa6", "#fff"]
      : style === "weather-warning"
        ? ["#d00020", "#fff"]
        : style === "weather-advisory"
          ? ["#ffd000", "#222"]
          : style === "volcano-warning"
            ? ["#d00020", "#fff"]
            : style === "eruption-flash"
              ? ["#c00000", "#fff"]
              : style === "volcano-forecast"
                ? ["#777", "#fff"]
          : ["#777", "#fff"]);
    badge.style.background = colors[0];
    badge.style.color = colors[1];
    if (style === "weather-special-warning") {
      badge.style.outline = "2px solid #d9d9d9";
      badge.style.outlineOffset = "-2px";
    }
    block.appendChild(badge);
  }
  const texts = document.createElement("div");
  if (hasWeatherContext) {
    const context = textNode("primary weather-context", explicitHeading || item.primaryText.slice(0, contextEnd));
    if (item.hideBadge) context.style.gridColumn = "1 / -1";
    block.appendChild(context);
  }
  texts.className = "texts";
  texts.appendChild(textNode("primary", hasWeatherContext && !explicitHeading ? item.primaryText.slice(contextEnd + 1) : item.primaryText));
  if (item.secondaryText) {
    texts.appendChild(textNode("secondary", item.secondaryText));
  }
  block.appendChild(texts);
  return block;
}

function renderVerticalEewCard(card) {
  const panel = document.createElement("article");
  panel.className = `vertical-eew-card${card.isCancelled ? " cancelled" : ""}`;
  const areas = document.createElement("div");
  areas.className = "vertical-eew-areas";
  // Keep each declared display label intact; never split a name at spaces or punctuation.
  const labels = card.isCancelled ? [card.message] : card.areas;
  for (const label of labels ?? []) {
    const area = document.createElement("span");
    area.className = "vertical-eew-area";
    area.textContent = label;
    areas.appendChild(area);
  }
  panel.appendChild(areas);
  const rail = textNode("vertical-eew-rail", "");
  rail.appendChild(textNode("vertical-eew-title", card.title));
  if (!card.isCancelled) rail.appendChild(textNode("vertical-eew-message", card.message));
  panel.appendChild(rail);
  panel.appendChild(textNode("vertical-eew-instruction", card.instruction));
  return panel;
}

function fitVerticalEew() {
  // Fit the actual rendered content, including long/unknown names and many regions.
  // No region is discarded to avoid overlap, and fitting never changes the source text.
  for (const panel of blocks.querySelectorAll(".vertical-eew-card")) {
    for (const [selector, maximum] of [[".vertical-eew-title", 56], [".vertical-eew-message", 42],
      [".vertical-eew-areas", 48], [".vertical-eew-instruction", 24]]) {
      const node = panel.querySelector(selector);
      if (!node) continue;
      node.style.fontSize = `${maximum}px`;
      if (node.scrollWidth <= node.clientWidth + 1 && node.scrollHeight <= node.clientHeight + 1) continue;
      let low = 1;
      let high = maximum;
      for (let step = 0; step < 12; step++) {
        const size = (low + high) / 2;
        node.style.fontSize = `${size}px`;
        if (node.scrollWidth <= node.clientWidth + 1 && node.scrollHeight <= node.clientHeight + 1) low = size;
        else high = size;
      }
      node.style.fontSize = `${Math.floor(low * 10) / 10}px`;
    }
  }
}

function applySnapshot(state) {
  applyOutputTransform(state.outputTransform);
  canvas.className = String(state.backgroundMode ?? "Transparent").toLowerCase();
  banner.textContent = state.rehearsalLabel ?? "";
  banner.hidden = !state.rehearsalLabel;
  content.hidden = !state.hasProgram;
  blocks.replaceChildren();
  const items = state.blocks ?? [];
  const isEew = items.some(item => String(item.styleToken ?? "").startsWith("eew-"));
  const eewHeaderCount = items.filter(item => {
    const style = String(item.styleToken ?? "");
    return style === "eew-header" || style === "eew-header-cancel" || style === "eew-header-test";
  }).length;
  const cards = items.filter(item => item.eewCard).map(item => item.eewCard);
  const vertical = isEew && state.useVerticalEewWarningLayout === true &&
    cards.length > 0 && cards.length === eewHeaderCount;
  const alignLeft = vertical && state.alignVerticalEewWarningsLeft === true;
  content.className = `${isEew ? "eew" : "subtitle"}${eewHeaderCount > 1 ? " concurrent" : ""}${vertical ? " vertical" : ""}${alignLeft ? " left-aligned" : ""}${state.rehearsalLabel ? " has-banner" : ""}`;
  if (vertical) {
    for (const card of cards) blocks.appendChild(renderVerticalEewCard(card));
    fitVerticalEew();
  } else {
    let previousBadge = "";
    for (const item of items) {
      blocks.appendChild(renderBlock(item, previousBadge));
      if (item.badge) previousBadge = item.badge;
    }
  }
  canvas.style.setProperty("--font-scale", String(Number(state.fontScale) || 1));
  canvas.style.setProperty("--letter-spacing", `${Number(state.letterSpacingEm) || 0}em`);
  canvas.style.setProperty("--line-spacing", String(Number(state.lineSpacing) || 1));
  const indicatorText = state.pageIndicator ?? "";
  pageIndicator.textContent = indicatorText;
  pageIndicator.hidden = !indicatorText;
  document.body.setAttribute("aria-label", state.accessibleText ?? "");
  if (handlesAudio) applyAudioCommand(state);
}

function reportAudioResult(sequence, result) {
  if (!sequence || lastReportedAudioResults.get(sequence) === result) return;
  lastReportedAudioResults.set(sequence, result);
  const query = new URLSearchParams({
    token,
    sequence: String(sequence),
    result
  });
  void fetch(`/audio-status?${query}`, {
    method: "POST",
    cache: "no-store",
    keepalive: true
  }).catch(() => {});
}

function stopAlertAudio(result = "") {
  const sequence = activeAudioSequence;
  activeAudioSequence = 0;
  alertAudio.pause();
  alertAudio.removeAttribute("src");
  alertAudio.load();
  if (sequence && result) reportAudioResult(sequence, result);
}

function applyAudioCommand(state) {
  const sequence = Number(state.audioSequence) || 0;
  if (sequence <= lastAudioSequence) return;
  lastAudioSequence = sequence;
  const action = String(state.audioAction ?? "");
  if (action === "stop") {
    stopAlertAudio("Stopped");
    return;
  }

  if (action !== "play") return;
  const issuedAt = Date.parse(String(state.audioIssuedAtUtc ?? ""));
  if (!Number.isFinite(issuedAt) || Date.now() - issuedAt > maximumAudioCommandAgeMilliseconds) {
    reportAudioResult(sequence, "SkippedStale");
    return;
  }

  stopAlertAudio("Interrupted");
  activeAudioSequence = sequence;
  alertAudio.volume = 1;
  alertAudio.src = `/audio/${sequence}?token=${encodeURIComponent(token)}`;
  void alertAudio.play().then(() => {
    if (activeAudioSequence === sequence) reportAudioResult(sequence, "Started");
  }).catch(() => {
    if (activeAudioSequence === sequence) activeAudioSequence = 0;
    reportAudioResult(sequence, "Failed");
  });
}

alertAudio.addEventListener("ended", () => {
  const sequence = activeAudioSequence;
  activeAudioSequence = 0;
  if (sequence) reportAudioResult(sequence, "Completed");
});

alertAudio.addEventListener("error", () => {
  const sequence = activeAudioSequence;
  activeAudioSequence = 0;
  if (sequence) reportAudioResult(sequence, "Failed");
});

async function loadCurrentState() {
  const query = new URLSearchParams({ token, view });
  const response = await fetch(`/state?${query}`, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }
  applySnapshot(await response.json());
}

function connect() {
  const query = new URLSearchParams({ token, view });
  const events = new EventSource(`/events?${query}`);
  events.onmessage = event => applySnapshot(JSON.parse(event.data));
  events.onerror = () => { void loadCurrentState().catch(() => {}); };
}

window.addEventListener("resize", resizeCanvas);
resizeCanvas();
if (monitorMode) {
  window.addEventListener("message", event => {
    if (event.origin !== window.location.origin || event.source !== window.parent || event.data?.type !== "cdi-monitor") return;
    applySnapshot(event.data.state);
  });
} else {
  void loadCurrentState().then(connect, connect);
}
