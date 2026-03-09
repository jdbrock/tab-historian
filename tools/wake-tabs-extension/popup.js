const btn = document.getElementById("wake");
const cancelBtn = document.getElementById("cancel");
const skipBtn = document.getElementById("skip");
const status = document.getElementById("status");

btn.addEventListener("click", async () => {
  btn.disabled = true;
  chrome.runtime.sendMessage({ action: "wake" });
  pollStatus();
});

cancelBtn.addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ action: "cancel" });
  pollStatus();
});

skipBtn.addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ action: "skip" });
  pollStatus();
});

async function pollStatus() {
  const s = await chrome.runtime.sendMessage({ action: "status" });

  if (s.done) {
    status.textContent = `Done! Activated ${s.activated} tabs (${s.skipped} skipped).`;
  } else if (s.batchInProgress) {
    status.textContent = `Waking tabs... ${s.activated} activated, ${s.remaining} remaining.`;
  } else if (s.running && s.remaining > 0) {
    status.textContent = `Waiting between batches. ${s.activated} activated, ${s.remaining} remaining. Next batch in ≤20 min.`;
  } else if (s.running) {
    status.textContent = `Starting...`;
  } else {
    status.textContent = "";
  }

  const isActive = s.running || s.batchInProgress;
  const isWaiting = s.running && !s.batchInProgress && s.remaining > 0;
  btn.disabled = isActive;
  btn.style.display = isActive ? "none" : "block";
  cancelBtn.style.display = isActive ? "block" : "none";
  skipBtn.style.display = isWaiting ? "block" : "none";

  if (isActive) {
    setTimeout(pollStatus, 2000);
  }
}

pollStatus();
