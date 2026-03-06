chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.action === "wake") {
    wakeAllTabs();
    sendResponse({ started: true });
  }
  if (msg.action === "status") {
    sendResponse({ ...state });
  }
  return true;
});

const state = { running: false, total: 0, activated: 0, windows: 0, done: false, skipped: 0 };

async function wakeAllTabs() {
  state.running = true;
  state.done = false;
  state.activated = 0;
  state.skipped = 0;

  const windows = await chrome.windows.getAll();
  const allTabs = await chrome.tabs.query({});

  state.total = allTabs.length;
  state.windows = windows.length;

  // Group tabs by window
  const tabsByWindow = new Map();
  for (const tab of allTabs) {
    if (!tabsByWindow.has(tab.windowId)) {
      tabsByWindow.set(tab.windowId, []);
    }
    tabsByWindow.get(tab.windowId).push(tab);
  }

  // Process each window: activate every tab, then restore the original active tab
  for (const [windowId, tabs] of tabsByWindow) {
    const originalActive = tabs.find(t => t.active);

    for (const tab of tabs) {
      if (tab.active) {
        state.skipped++;
        continue;
      }

      try {
        await chrome.tabs.update(tab.id, { active: true });
        // Brief pause — just enough for Chrome to register the activation
        await new Promise(r => setTimeout(r, 100));
        state.activated++;
      } catch (e) {
        state.skipped++;
      }
    }

    // Restore original active tab in this window
    try {
      if (originalActive) {
        await chrome.tabs.update(originalActive.id, { active: true });
      }
    } catch (e) {
      // Window may have closed
    }
  }

  state.running = false;
  state.done = true;
}
