const BATCH_SIZE = 50;
const BATCH_INTERVAL_MIN = 20;
const TAB_LOAD_TIMEOUT_MS = 15000;
const BATCH_ALARM = "next-batch";

const defaultState = {
  running: false,
  batchInProgress: false,
  done: false,
  total: 0,
  activated: 0,
  skipped: 0,
  windows: 0,
  remaining: 0,
  // Persisted across batches
  pendingTabs: [],         // [{id, windowId}]
  activatedTabs: [],       // [tabId] — tabs to discard at start of next batch
  originalActives: {},     // windowId -> tabId
};

async function getState() {
  const { wakeState } = await chrome.storage.local.get("wakeState");
  return wakeState || { ...defaultState };
}

async function setState(patch) {
  const state = await getState();
  Object.assign(state, patch);
  state.remaining = state.pendingTabs.length;
  await chrome.storage.local.set({ wakeState: state });
  return state;
}

// Wait for a tab to finish loading, with timeout
function waitForTabLoad(tabId) {
  return new Promise(resolve => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      resolve("timeout");
    }, TAB_LOAD_TIMEOUT_MS);

    function listener(updatedId, changeInfo) {
      if (updatedId === tabId && changeInfo.status === "complete") {
        clearTimeout(timeout);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve("loaded");
      }
    }

    chrome.tabs.onUpdated.addListener(listener);

    // Check if already complete
    chrome.tabs.get(tabId).then(tab => {
      if (tab.status === "complete") {
        clearTimeout(timeout);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve("already-loaded");
      }
    }).catch(() => {
      clearTimeout(timeout);
      chrome.tabs.onUpdated.removeListener(listener);
      resolve("error");
    });
  });
}

async function startWake() {
  const windows = await chrome.windows.getAll();
  const allTabs = await chrome.tabs.query({});

  const originalActives = {};
  const pendingTabs = [];

  for (const tab of allTabs) {
    if (tab.active) {
      originalActives[tab.windowId] = tab.id;
    } else {
      pendingTabs.push({ id: tab.id, windowId: tab.windowId });
    }
  }

  await setState({
    running: true,
    batchInProgress: false,
    done: false,
    total: allTabs.length,
    activated: 0,
    skipped: 0,
    windows: windows.length,
    pendingTabs,
    originalActives,
  });

  await runNextBatch();
}

async function runNextBatch() {
  let state = await getState();
  if (!state.running || state.pendingTabs.length === 0) {
    await setState({ running: false, batchInProgress: false, done: true, pendingTabs: [], activatedTabs: [] });
    chrome.alarms.clear(BATCH_ALARM);
    return;
  }

  // Discard previously activated tabs to free memory
  if (state.activatedTabs && state.activatedTabs.length > 0) {
    for (const tabId of state.activatedTabs) {
      try {
        await chrome.tabs.discard(tabId);
      } catch (e) {
        // Tab may have been closed or already discarded
      }
    }
    state = await setState({ activatedTabs: [] });
  }

  const batch = state.pendingTabs.slice(0, BATCH_SIZE);
  const rest = state.pendingTabs.slice(BATCH_SIZE);

  state = await setState({ batchInProgress: true, pendingTabs: rest });

  const touchedWindows = new Set();
  const newlyActivated = [];

  for (const { id, windowId } of batch) {
    try {
      await chrome.tabs.update(id, { active: true });
      touchedWindows.add(windowId);
      await new Promise(r => setTimeout(r, 500));
      newlyActivated.push(id);
      state = await setState({ activated: state.activated + 1 });
    } catch (e) {
      state = await setState({ skipped: state.skipped + 1 });
    }
  }

  // Restore original active tabs in windows we touched
  for (const windowId of touchedWindows) {
    const originalTabId = state.originalActives[windowId];
    if (originalTabId) {
      try {
        await chrome.tabs.update(originalTabId, { active: true });
      } catch (e) {
        // Window or tab may have closed
      }
    }
  }

  if (state.pendingTabs.length === 0) {
    // Don't discard the final batch — leave them loaded
    await setState({ running: false, batchInProgress: false, done: true, activatedTabs: [] });
    chrome.alarms.clear(BATCH_ALARM);
  } else {
    await setState({ batchInProgress: false, activatedTabs: newlyActivated });
    chrome.alarms.create(BATCH_ALARM, { delayInMinutes: BATCH_INTERVAL_MIN });
  }
}

// Alarm triggers next batch
chrome.alarms.onAlarm.addListener(async alarm => {
  if (alarm.name === BATCH_ALARM) {
    await runNextBatch();
  }
});

// Resume after reload: if there's a pending run, schedule the next batch
chrome.runtime.onStartup.addListener(async () => {
  const state = await getState();
  if (state.running && !state.batchInProgress && state.pendingTabs.length > 0) {
    chrome.alarms.create(BATCH_ALARM, { delayInMinutes: 0.1 });
  }
});

// Also handle extension install/reload (onStartup doesn't fire for reloads)
(async () => {
  const state = await getState();
  if (state.running && state.pendingTabs.length > 0) {
    // If a batch was in progress when we were killed, it's lost — just mark it not in progress
    if (state.batchInProgress) {
      await setState({ batchInProgress: false });
    }
    // Backfill activatedTabs if missing (e.g. upgraded mid-run)
    if (!state.activatedTabs || state.activatedTabs.length === 0) {
      const allTabs = await chrome.tabs.query({});
      const pendingSet = new Set(state.pendingTabs.map(t => t.id));
      const activeSet = new Set(Object.values(state.originalActives));
      const alreadyActivated = allTabs
        .filter(t => !pendingSet.has(t.id) && !activeSet.has(t.id))
        .map(t => t.id);
      await setState({ activatedTabs: alreadyActivated });
    }
    // Check if there's already an alarm pending; if not, create one
    const existing = await chrome.alarms.get(BATCH_ALARM);
    if (!existing) {
      chrome.alarms.create(BATCH_ALARM, { delayInMinutes: 0.1 });
    }
  }
})();

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.action === "wake") {
    startWake();
    sendResponse({ started: true });
  }
  if (msg.action === "skip") {
    chrome.alarms.clear(BATCH_ALARM);
    runNextBatch();
    sendResponse({ skipped: true });
  }
  if (msg.action === "cancel") {
    chrome.alarms.clear(BATCH_ALARM);
    setState({ running: false, batchInProgress: false, done: false, pendingTabs: [] })
      .then(s => sendResponse(s));
    return true;
  }
  if (msg.action === "status") {
    getState().then(s => sendResponse(s));
    return true;
  }
  return true;
});
