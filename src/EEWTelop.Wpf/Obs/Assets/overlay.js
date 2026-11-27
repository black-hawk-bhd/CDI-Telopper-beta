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
const alertAudio = document.getElementById("alertAudio");
const subtitleImage = document.getElementById("subtitleImage");
let subtitleImageUrl = "";
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
  "intensity", "comment", "tsunami", "tsunami-cancel", "overseas-tsunami-observation", "weather-advisory",
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

function renderBlock(item, previousBadge, alignsCaption) {
  const block = document.createElement("div");
  const style = allowedStyles.has(item.styleToken) ? item.styleToken : "summary";
  block.className = `block ${style}`;
  const weatherStyle = style.startsWith("weather-");
  const contextEnd = (item.primaryText || "").indexOf("\n");
  const explicitHeading = item.weatherHeading || "";
  const hasWeatherContext = weatherStyle && item.badge && (explicitHeading || contextEnd > 0 &&
    item.primaryText.slice(0, contextEnd).includes("｜"));
  if (hasWeatherContext) block.classList.add("weather-area-heading");
  if (alignsCaption) block.classList.add("aligned-label");
  const volcanoStyle = style.startsWith("volcano-") || style === "eruption-flash";
  const reservesBadge = (alignsCaption || style === "correction" || weatherStyle || volcanoStyle) &&
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
  if (alignsCaption && item.hideBadge) {
    block.appendChild(textNode("primary subtitle-label", item.subtitleBodyText != null ? item.badge : ""));
  }
  const texts = document.createElement("div");
  if (hasWeatherContext) {
    const context = textNode("primary weather-context", explicitHeading || item.primaryText.slice(0, contextEnd));
    if (item.hideBadge) context.style.gridColumn = "1 / -1";
    block.appendChild(context);
  }
  texts.className = "texts";
  const primaryText = alignsCaption && item.subtitleBodyText != null ? item.subtitleBodyText
    : hasWeatherContext && !explicitHeading ? item.primaryText.slice(contextEnd + 1) : item.primaryText;
  const primary = textNode("primary", primaryText);
  const labels = item.eewAreaLabels;
  if (style === "eew-areas" && labels?.map(label => label.name).join("　") === primaryText) {
    primary.replaceChildren();
    for (const [index, label] of labels.entries()) {
      if (index > 0) primary.append("　");
      const area = document.createElement("span");
      if (label.isChanged) area.className = "eew-area-changed";
      area.textContent = label.name;
      primary.appendChild(area);
    }
  }
  texts.appendChild(primary);
  if (item.secondaryText) {
    texts.appendChild(textNode("secondary", item.secondaryText));
  }
  if (item.eewEstimateText) {
    texts.appendChild(textNode("eew-estimates", item.eewEstimateText));
  }
  block.appendChild(texts);
  return block;
}

function renderVerticalEewCard(card, areaLabels, estimateText) {
  const panel = document.createElement("article");
  panel.className = `vertical-eew-card${card.isCancelled ? " cancelled" : ""}`;
  const areas = document.createElement("div");
  areas.className = "vertical-eew-areas";
  // Keep each declared display label intact; never split a name at spaces or punctuation.
  const labels = card.isCancelled ? [card.message] : card.areas;
  const changed = new Set(areaLabels?.filter(label => label.isChanged).map(label => label.name));
  for (const label of labels ?? []) {
    const area = document.createElement("span");
    area.className = "vertical-eew-area";
    if (!card.isCancelled && changed.has(label)) area.classList.add("eew-area-changed");
    area.textContent = label;
    areas.appendChild(area);
  }
  panel.appendChild(areas);
  const rail = textNode("vertical-eew-rail", "");
  rail.appendChild(textNode("vertical-eew-title", card.title));
  if (!card.isCancelled) rail.appendChild(textNode("vertical-eew-message", card.message));
  panel.appendChild(rail);
  panel.appendChild(textNode("vertical-eew-instruction", card.instruction));
  if (!card.isCancelled && estimateText) {
    panel.classList.add("has-estimates");
    panel.appendChild(textNode("vertical-eew-estimates", estimateText));
  }
  return panel;
}

function fitVerticalEew() {
  // Fit the actual rendered content, including long/unknown names and many regions.
  // No region is discarded to avoid overlap, and fitting never changes the source text.
  for (const panel of blocks.querySelectorAll(".vertical-eew-card")) {
    for (const [selector, maximum] of [[".vertical-eew-title", 56], [".vertical-eew-message", 42],
      [".vertical-eew-areas", 48], [".vertical-eew-instruction", 24], [".vertical-eew-estimates", 22]]) {
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

function updateSubtitleImageVisibility() {
  subtitleImage.hidden = !subtitleImageUrl || subtitleImage.currentSrc !== subtitleImageUrl ||
    !subtitleImage.complete || subtitleImage.naturalWidth === 0;
}

subtitleImage.addEventListener("load", updateSubtitleImageVisibility);
subtitleImage.addEventListener("error", updateSubtitleImageVisibility);

function applySubtitleImage(image) {
  if (!image || !/^[0-9a-f]{64}$/.test(image.id)) {
    subtitleImageUrl = "";
    subtitleImage.hidden = true;
    return;
  }

  subtitleImage.style.left = `${image.x}px`;
  subtitleImage.style.top = `${image.y}px`;
  subtitleImage.style.width = `${image.width}px`;
  const query = new URLSearchParams({ token });
  subtitleImageUrl = new URL(`/subtitle-image/${image.id}?${query}`, window.location.origin).href;
  if (subtitleImage.src !== subtitleImageUrl) subtitleImage.src = subtitleImageUrl;
  updateSubtitleImageVisibility();
}

function applySnapshot(state) {
  applyOutputTransform(state.outputTransform);
  canvas.className = String(state.backgroundMode ?? "Transparent").toLowerCase();
  const fontFamily = state.fontFamilyName;
  canvas.style.fontFamily = typeof fontFamily === "string" && fontFamily && fontFamily !== "BIZ UDPGothic"
    ? `"${CSS.escape(fontFamily)}", var(--telop-font-fallback)` : "";
  banner.textContent = state.rehearsalLabel ?? "";
  banner.hidden = !state.rehearsalLabel;
  content.hidden = !state.hasProgram;
  applySubtitleImage(state.hasProgram ? state.subtitleImage : null);
  blocks.replaceChildren();
  blocks.classList.remove("aligned-labels");
  const items = state.blocks ?? [];
  blocks.classList.toggle("overseas-tsunami-observations",
    items.some(item => item.styleToken === "overseas-tsunami-observation"));
  const isEew = items.some(item => String(item.styleToken ?? "").startsWith("eew-"));
  const eewHeaderCount = items.filter(item => {
    const style = String(item.styleToken ?? "");
    return style === "eew-header" || style === "eew-header-cancel" || style === "eew-header-test";
  }).length;
  const cards = [];
  for (const item of items) {
    if (item.eewCard) cards.push({ item, estimateText: null });
    if (item.eewEstimateText && cards.length > 0) cards.at(-1).estimateText = item.eewEstimateText;
  }
  const vertical = isEew && state.useVerticalEewWarningLayout === true &&
    cards.length > 0 && cards.length === eewHeaderCount;
  const alignLeft = vertical && state.alignVerticalEewWarningsLeft === true;
  content.className = `${isEew ? "eew" : "subtitle"}${eewHeaderCount > 1 ? " concurrent" : ""}${vertical ? " vertical" : ""}${alignLeft ? " left-aligned" : ""}${state.rehearsalLabel ? " has-banner" : ""}`;
  if (vertical) {
    for (const { item, estimateText } of cards) {
      blocks.appendChild(renderVerticalEewCard(item.eewCard, item.eewAreaLabels, estimateText));
    }
    fitVerticalEew();
  } else {
    let previousBadge = "";
    let previousAlignedStyle = "";
    for (const item of items) {
      const hasBadge = Boolean(item.badge?.trim());
      const alignsCaption = (item.styleToken === "intensity" || item.styleToken === "tsunami") &&
        (hasBadge ? !item.hideBadge || item.subtitleBodyText != null : previousAlignedStyle === item.styleToken);
      blocks.appendChild(renderBlock(item, previousBadge, alignsCaption));
      if (alignsCaption) blocks.classList.add("aligned-labels");
      previousAlignedStyle = alignsCaption ? item.styleToken : "";
      if (hasBadge) previousBadge = item.badge;
    }
  }
  canvas.style.setProperty("--font-scale", String(Number(state.fontScale) || 1));
  canvas.style.setProperty("--letter-spacing", `${Number(state.letterSpacingEm) || 0}em`);
  canvas.style.setProperty("--line-spacing", String(Number(state.lineSpacing) || 1));
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
