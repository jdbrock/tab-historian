const btn = document.getElementById("wake");
const status = document.getElementById("status");

btn.addEventListener("click", async () => {
  btn.disabled = true;
  chrome.runtime.sendMessage({ action: "wake" });
  pollStatus();
});

async function pollStatus() {
  const s = await chrome.runtime.sendMessage({ action: "status" });
  status.textContent = s.done
    ? `Done! Activated ${s.activated} tabs (${s.skipped} skipped).`
    : s.running
      ? `Activating ${s.activated}/${s.total} tabs across ${s.windows} windows...`
      : `Ready. ${s.total} tabs across ${s.windows} windows.`;

  if (s.running) {
    btn.disabled = true;
    setTimeout(pollStatus, 500);
  } else {
    btn.disabled = false;
  }
}

// Show current status on open
pollStatus();
