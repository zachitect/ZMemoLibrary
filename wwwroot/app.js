(() => {
  const state = {
    items: [],
    selected: new Map(),
    expanded: new Set(),
    histories: new Map(),
    lastSelectedIndex: null,
    query: ""
  };

  const elements = {
    search: document.querySelector("#search"),
    clearSearch: document.querySelector("#clearSearch"),
    rescan: document.querySelector("#rescan"),
    themeToggle: document.querySelector("#themeToggle"),
    status: document.querySelector("#status"),
    list: document.querySelector("#list"),
    issues: document.querySelector("#issues"),
    issuesSummary: document.querySelector("#issuesSummary"),
    issuesList: document.querySelector("#issuesList"),
    selectionCount: document.querySelector("#selectionCount"),
    clearSelection: document.querySelector("#clearSelection"),
    downloadSelected: document.querySelector("#downloadSelected"),
    deleteSelected: document.querySelector("#deleteSelected"),
    uploadMarkdown: document.querySelector("#uploadMarkdown"),
    uploadInput: document.querySelector("#uploadInput"),
    catalogueSection: document.querySelector(".catalogue-section"),
    dropOverlay: document.querySelector("#dropOverlay"),
    deleteDialog: document.querySelector("#deleteDialog"),
    deleteMessage: document.querySelector("#deleteMessage"),
    confirmDelete: document.querySelector("#confirmDelete"),
    cancelDelete: document.querySelector("#cancelDelete"),
    operationResultsDialog: document.querySelector("#operationResultsDialog"),
    operationResultsTitle: document.querySelector("#operationResultsTitle"),
    operationResults: document.querySelector("#operationResults"),
    closeOperationResults: document.querySelector("#closeOperationResults"),
    viewer: document.querySelector("#viewer"),
    viewerStatus: document.querySelector("#viewerStatus"),
    viewerTitle: document.querySelector("#viewerTitle"),
    viewerMeta: document.querySelector("#viewerMeta"),
    viewerBody: document.querySelector("#viewerBody"),
    copyViewerContent: document.querySelector("#copyViewerContent"),
    downloadViewerContent: document.querySelector("#downloadViewerContent"),
    closeViewer: document.querySelector("#closeViewer"),
    promptLinks: document.querySelectorAll("[data-prompt-key]")
  };

  let searchTimer = null;
  let viewerContent = "";
  let viewerDownloadUrl = "";

  function setTheme(theme) {
    document.documentElement.dataset.theme = theme;
    document.body.dataset.theme = theme;
    const switchToDay = theme === "dark";
    elements.themeToggle.textContent = switchToDay ? "☀" : "☾";
    elements.themeToggle.setAttribute("aria-label", switchToDay ? "Switch to day theme" : "Switch to night theme");
    elements.themeToggle.title = switchToDay ? "Switch to day theme" : "Switch to night theme";
  }

  function setInitialTheme() {
    const hour = new Date().getHours();
    setTheme(hour >= 18 || hour < 6 ? "dark" : "light");
  }

  async function request(url, options) {
    const response = await fetch(url, options);
    if (response.status === 401) {
        window.location.assign("/access");
        throw new Error("Access expired. Redirecting to unlock page...");
    }
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`;
      try {
        const payload = await response.json();
        if (payload.error) message = payload.error;
      } catch { }
      throw new Error(message);
    }
    return response;
  }

  async function load() {
    elements.status.textContent = "Loading knowledge catalogue...";
    try {
      const [statusResponse, knowledgeResponse] = await Promise.all([
        request("/api/library"),
        request(`/api/knowledge?query=${encodeURIComponent(state.query)}`)
      ]);
      const status = await statusResponse.json();
      state.items = await knowledgeResponse.json();
      for (const item of state.items) {
        if (item.versionKey && state.selected.has(item.versionKey))
          state.selected.set(item.versionKey, item);
      }
      renderStatus(status);
      renderIssues(status.issues);
      state.lastSelectedIndex = null;
      renderList();
    } catch (error) {
      elements.status.innerHTML = "";
      const text = document.createElement("span");
      text.className = "error";
      text.textContent = error.message;
      elements.status.append(text);
      elements.list.innerHTML = '<div class="empty">The catalogue could not be loaded.</div>';
    }
  }

  function renderStatus(status) {
    elements.status.innerHTML = "";
    const left = document.createElement("span");
    left.textContent = status.isAvailable
      ? `${status.currentSeriesCount} current entries · ${status.ambiguousSeriesCount} ambiguous series · ${status.versionCount} versions`
      : "Knowledge library unavailable";
    if (!status.isAvailable) left.className = "error";

    const right = document.createElement("span");
    right.className = "root-path";
    right.textContent = status.rootPath;
    right.title = `Knowledge source directory · Scanned ${new Date(status.scannedAt).toLocaleString()}`;
    elements.status.append(left, right);
  }

  async function viewPrompt(promptKey, title) {
    try {
      const response = await request(`/api/prompts/${promptKey}`);
      const markdown = await response.text();
      viewerContent = markdown;
      viewerDownloadUrl = `/api/prompts/${promptKey}/download`;
      elements.viewerStatus.textContent = "Embedded LLM prompt";
      elements.viewerTitle.textContent = title;
      elements.viewerMeta.textContent = "Reference prompt included with ZMemoLibrary";
      elements.viewerBody.innerHTML = renderMarkdown(markdown);
      elements.viewer.showModal();
    } catch (error) {
      alert(error.message);
    }
  }

  function renderIssues(issues) {
    elements.issues.hidden = !issues || issues.length === 0;
    elements.issuesList.innerHTML = "";
    if (!issues || issues.length === 0) return;
    elements.issuesSummary.textContent = `${issues.length} scan issue${issues.length === 1 ? "" : "s"}`;
    for (const issue of issues) {
      const item = document.createElement("li");
      item.textContent = `${issue.path}: ${issue.message}`;
      elements.issuesList.append(item);
    }
  }

  function renderList() {
    elements.list.innerHTML = "";
    if (state.items.length === 0) {
      const empty = document.createElement("div");
      empty.className = "knowledge-row empty";
      empty.textContent = state.query ? "No current knowledge matches this search." : "No current knowledge files are available.";
      elements.list.append(empty);
      updateSelectionBar();
      return;
    }

    const connectedIds = new Set(
      [...state.selected.values()]
        .flatMap(item => item.connectedIds || [])
        .map(id => id.toLowerCase())
    );

    state.items.forEach((item, index) => {
      const row = document.createElement("article");
      row.className = "knowledge-row";
      row.dataset.id = item.id;
      row.tabIndex = -1;
      if (item.isAmbiguous) row.classList.add("ambiguous");
      if (connectedIds.has(item.id.toLowerCase())) row.classList.add("connected");
      if (item.versionKey && state.selected.has(item.versionKey)) row.classList.add("selected");

      const top = document.createElement("div");
      top.className = "row-top";
      const text = document.createElement("div");
      const metadata = document.createElement("div");
      metadata.className = "knowledge-meta";
      const guid = document.createElement("div");
      guid.className = "guid";
      guid.textContent = item.id;
      const updated = document.createElement("div");
      updated.className = "updated";
      updated.textContent = `Updated ${item.updated}`;
      metadata.append(guid, updated);
      const title = document.createElement("div");
      title.className = "title";
      title.textContent = item.title;
      const summary = document.createElement("p");
      summary.className = "summary";
      summary.textContent = item.summary;
      text.append(title, metadata, summary);
      top.append(text);

      if (item.versionCount > 1 || item.isAmbiguous) {
        const versions = document.createElement("button");
        versions.className = "versions-button";
        versions.type = "button";
        versions.textContent = `${item.versionCount} versions ${state.expanded.has(item.id) ? "▾" : "▸"}`;
        versions.addEventListener("click", event => {
          event.stopPropagation();
          toggleHistory(item.id);
        });
        top.append(versions);
      }

      row.append(top);
      if (item.warning) {
        const warning = document.createElement("div");
        warning.className = "updated warning";
        warning.textContent = item.warning;
        row.append(warning);
      }

      if (!item.isAmbiguous) {
        row.addEventListener("click", event => selectRow(item, index, event));
        row.addEventListener("dblclick", () => viewVersion(item.versionKey));
      }
      elements.list.append(row);

      if (state.expanded.has(item.id))
        elements.list.append(renderHistory(item.id));
    });

    updateSelectionBar();
  }

  function selectRow(item, index, event) {
    if (!item.versionKey) return;
    const preserve = event.ctrlKey || event.metaKey;

    if (event.shiftKey && state.lastSelectedIndex !== null) {
      if (!preserve) state.selected.clear();
      const start = Math.min(state.lastSelectedIndex, index);
      const end = Math.max(state.lastSelectedIndex, index);
      for (let i = start; i <= end; i++) {
        const rangeItem = state.items[i];
        if (rangeItem.versionKey && !rangeItem.isAmbiguous)
          state.selected.set(rangeItem.versionKey, rangeItem);
      }
    } else if (preserve) {
      if (state.selected.has(item.versionKey)) state.selected.delete(item.versionKey);
      else state.selected.set(item.versionKey, item);
      state.lastSelectedIndex = index;
    } else {
      state.selected.clear();
      state.selected.set(item.versionKey, item);
      state.lastSelectedIndex = index;
    }

    elements.list.focus({ preventScroll: true });
    renderList();
  }

  async function toggleHistory(id) {
    if (state.expanded.has(id)) {
      state.expanded.delete(id);
      renderList();
      return;
    }

    if (!state.histories.has(id)) {
      try {
        const response = await request(`/api/knowledge/${id}/versions`);
        state.histories.set(id, await response.json());
      } catch (error) {
        alert(error.message);
        return;
      }
    }

    state.expanded.add(id);
    renderList();
  }

  function renderHistory(id) {
    const history = document.createElement("section");
    history.className = "history";
    for (const version of state.histories.get(id) || []) {
      const item = document.createElement("div");
      item.className = "history-item";
      const text = document.createElement("div");
      const title = document.createElement("div");
      title.className = "history-title";
      title.textContent = version.title;
      const meta = document.createElement("div");
      meta.className = "history-meta";
      meta.textContent = `${version.isCurrent ? "Current" : "Historical"} · Updated ${version.updated} · ${version.fileName}`;
      text.append(title, meta);

      const view = document.createElement("button");
      view.type = "button";
      view.className = "small-button";
      view.textContent = "View";
      view.addEventListener("click", () => viewVersion(version.versionKey));

      const download = document.createElement("button");
      download.type = "button";
      download.className = "small-button download-version";
      download.textContent = "Download";
      download.addEventListener("click", () => window.location.assign(`/api/versions/${version.versionKey}/download`));

      item.append(text, view, download);
      history.append(item);
    }
    return history;
  }

  function renderMarkdown(markdown) {
    const escaped = escapeHtml(markdown).replace(/\r\n?/g, "\n");
    const codeBlocks = [];
    const protectedText = escaped.replace(/```([^\n]*)\n([\s\S]*?)```/g, (_, language, code) => {
      const index = codeBlocks.length;
      const languageClass = language.trim() ? ` class="language-${escapeAttribute(language.trim())}"` : "";
      codeBlocks.push(`<pre><code${languageClass}>${code.replace(/\n$/, "")}</code></pre>`);
      return `\n@@CODEBLOCK${index}@@\n`;
    });

    const lines = protectedText.split("\n");
    const html = [];
    let paragraph = [];
    let listType = null;
    let blockquote = [];

    function flushParagraph() {
      if (paragraph.length === 0) return;
      html.push(`<p>${formatInline(paragraph.join(" "))}</p>`);
      paragraph = [];
    }

    function closeList() {
      if (!listType) return;
      html.push(`</${listType}>`);
      listType = null;
    }

    function flushBlockquote() {
      if (blockquote.length === 0) return;
      html.push(`<blockquote>${formatInline(blockquote.join(" "))}</blockquote>`);
      blockquote = [];
    }

    for (const line of lines) {
      const trimmed = line.trim();
      const codeMatch = trimmed.match(/^@@CODEBLOCK(\d+)@@$/);
      if (codeMatch) {
        flushParagraph();
        closeList();
        flushBlockquote();
        html.push(codeBlocks[Number(codeMatch[1])]);
        continue;
      }

      if (trimmed === "") {
        flushParagraph();
        closeList();
        flushBlockquote();
        continue;
      }

      const heading = line.match(/^(#{1,6})\s+(.+)$/);
      if (heading) {
        flushParagraph();
        closeList();
        flushBlockquote();
        const level = heading[1].length;
        html.push(`<h${level}>${formatInline(heading[2])}</h${level}>`);
        continue;
      }

      if (/^([-*_])(?:\s*\1){2,}\s*$/.test(trimmed)) {
        flushParagraph();
        closeList();
        flushBlockquote();
        html.push("<hr>");
        continue;
      }

      const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
      const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
      if (unordered || ordered) {
        flushParagraph();
        flushBlockquote();
        const requestedType = unordered ? "ul" : "ol";
        if (listType !== requestedType) {
          closeList();
          listType = requestedType;
          html.push(`<${listType}>`);
        }
        html.push(`<li>${formatInline((unordered || ordered)[1])}</li>`);
        continue;
      }

      const quote = line.match(/^\s*>\s?(.*)$/);
      if (quote) {
        flushParagraph();
        closeList();
        blockquote.push(quote[1]);
        continue;
      }

      closeList();
      flushBlockquote();
      paragraph.push(trimmed);
    }

    flushParagraph();
    closeList();
    flushBlockquote();
    return html.join("\n");
  }

  function formatInline(text) {
    return text
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/__([^_]+)__/g, "<strong>$1</strong>")
      .replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, "<em>$1</em>")
      .replace(/(?<!_)_([^_]+)_(?!_)/g, "<em>$1</em>")
      .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
  }

  function escapeHtml(value) {
    return value
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#039;");
  }

  function escapeAttribute(value) {
    return value.replace(/[^a-zA-Z0-9_-]/g, "");
  }

  async function viewVersion(versionKey) {
    try {
      const response = await request(`/api/versions/${versionKey}`);
      const version = await response.json();
      const downloadUrl = `/api/versions/${versionKey}/download`;
      const contentResponse = await request(downloadUrl);
      viewerContent = await contentResponse.text();
      viewerDownloadUrl = downloadUrl;
      elements.viewerStatus.textContent = `${version.isCurrent ? "Current version" : "Historical version"} · Updated ${version.updated}`;
      elements.viewerTitle.textContent = version.title;
      elements.viewerMeta.textContent = `${version.seriesId} · Created ${version.created}`;
      elements.viewerBody.innerHTML = renderMarkdown(version.body);
      elements.viewer.showModal();
    } catch (error) {
      alert(error.message);
    }
  }

  async function copyViewerContent() {
    if (!viewerContent) return;

    try {
      await navigator.clipboard.writeText(viewerContent);
      const originalText = elements.copyViewerContent.textContent;
      elements.copyViewerContent.textContent = "Copied";
      setTimeout(() => elements.copyViewerContent.textContent = originalText, 1200);
    } catch (error) {
      alert(`Could not copy content: ${error.message}`);
    }
  }

  function downloadViewerContent() {
    if (!viewerDownloadUrl) return;
    window.location.assign(viewerDownloadUrl);
  }

  async function uploadFiles(files) {
    const markdownFiles = [...files].filter(file => file.name.toLowerCase().endsWith(".md"));
    const rejected = [...files]
      .filter(file => !file.name.toLowerCase().endsWith(".md"))
      .map(file => ({ fileName: file.name, outcome: "Invalid", succeeded: false, message: "Only .md files are accepted." }));

    if (markdownFiles.length === 0) {
      if (rejected.length > 0) showOperationResults("Upload Results", rejected);
      return;
    }

    elements.uploadMarkdown.disabled = true;
    const formData = new FormData();
    for (const file of markdownFiles) formData.append("files", file, file.name);

    try {
      const response = await request("/api/library/upload", { method: "POST", body: formData });
      const results = rejected.concat(await response.json());
      showOperationResults("Upload Results", results);
      if (results.some(result => result.succeeded)) {
        state.histories.clear();
        state.expanded.clear();
        await load();
      }
    } catch (error) {
      showOperationResults("Upload Results", [{ fileName: "Upload", outcome: "Failed", succeeded: false, message: error.message }]);
    } finally {
      elements.uploadMarkdown.disabled = false;
      elements.uploadInput.value = "";
    }
  }

  function showOperationResults(title, results) {
    elements.operationResultsTitle.textContent = title;
    elements.operationResults.innerHTML = "";
    for (const result of results) {
      const item = document.createElement("li");
      item.className = "result-item";
      const outcome = document.createElement("div");
      outcome.className = `result-outcome ${result.succeeded ? "" : "error"}`;
      outcome.textContent = `${result.fileName}: ${result.outcome}`;
      const message = document.createElement("div");
      message.className = "result-message";
      message.textContent = result.message;
      item.append(outcome, message);
      elements.operationResults.append(item);
    }
    elements.operationResultsDialog.showModal();
  }

  function openDeleteConfirmation() {
    if (state.selected.size === 0) return;
    elements.deleteMessage.textContent = `Delete ${state.selected.size} selected knowledge series? This deletes every current and historical version in each selected series. All physical files will be moved to the server's Deleted folder for manual inspection and hidden from ZMemoLibrary. Individual historical versions cannot be deleted separately.`;
    elements.deleteDialog.showModal();
  }

  async function deleteSelected() {
    const versionKeys = [...state.selected.keys()];
    if (versionKeys.length === 0) return;

    elements.confirmDelete.disabled = true;
    try {
      const response = await request("/api/library/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ versionKeys })
      });
      const results = await response.json();
      for (const result of results) {
        if (result.succeeded && result.versionKey) state.selected.delete(result.versionKey);
      }
      elements.deleteDialog.close();
      showOperationResults("Delete Results", results);
      state.histories.clear();
      state.expanded.clear();
      await load();
    } catch (error) {
      elements.deleteDialog.close();
      showOperationResults("Delete Results", [{ fileName: "Delete", outcome: "Failed", succeeded: false, message: error.message }]);
    } finally {
      elements.confirmDelete.disabled = false;
    }
  }

  function isFileDrag(event) {
    return [...(event.dataTransfer?.types || [])].includes("Files");
  }

  function copySelected() {
    if (state.selected.size === 0) return;
    const text = [...state.selected.values()].map(item =>
      `${item.id}\n${item.title}\nUpdated ${item.updated}\n${item.summary}`
    ).join("\n\n");

    navigator.clipboard.writeText(text).then(() => {
      elements.selectionCount.textContent = `${state.selected.size} selected · copied displayed summaries`;
      setTimeout(updateSelectionBar, 1600);
    }).catch(error => alert(`Could not copy selection: ${error.message}`));
  }

  async function downloadSelected() {
    const selectedEntries = [...state.selected.values()];
    if (selectedEntries.length === 0) return;

    elements.downloadSelected.disabled = true;
    try {
      for (let index = 0; index < selectedEntries.length; index++) {
        const entry = selectedEntries[index];
        elements.selectionCount.textContent = `Downloading ${index + 1} of ${selectedEntries.length}...`;
        const response = await request(`/api/versions/${entry.versionKey}/download`);
        await downloadResponse(response, `${entry.id}.md`);
      }
    } catch (error) {
      alert(error.message);
    } finally {
      updateSelectionBar();
    }
  }

  async function downloadResponse(response, fallbackFileName) {
    const blob = await response.blob();
    const disposition = response.headers.get("Content-Disposition") || "";
    const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i);
    const fileName = match ? decodeURIComponent(match[1].replace(/\"$/, "")) : fallbackFileName;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  function updateSelectionBar() {
    const visibleSelected = state.items.filter(item => item.versionKey && state.selected.has(item.versionKey)).length;
    const total = state.selected.size;
    elements.selectionCount.textContent = total === 0
      ? "0 selected · Ctrl+C copies displayed summaries"
      : `${total} selected · ${visibleSelected} currently visible · Ctrl+C copies displayed summaries`;
    elements.clearSelection.disabled = total === 0;
    elements.deleteSelected.disabled = total === 0;
    elements.downloadSelected.disabled = total === 0;
  }

  elements.search.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      state.query = elements.search.value.trim();
      load();
    }, 250);
  });

  elements.clearSearch.addEventListener("click", () => {
    elements.search.value = "";
    state.query = "";
    load();
    elements.search.focus();
  });

  elements.themeToggle.addEventListener("click", () => {
    setTheme(document.body.dataset.theme === "dark" ? "light" : "dark");
  });

  elements.rescan.addEventListener("click", async () => {
    elements.rescan.disabled = true;
    try {
      await request("/api/rescan", { method: "POST" });
      state.histories.clear();
      state.expanded.clear();
      await load();
    } catch (error) {
      alert(error.message);
    } finally {
      elements.rescan.disabled = false;
    }
  });

  elements.clearSelection.addEventListener("click", () => {
    state.selected.clear();
    renderList();
  });
  elements.downloadSelected.addEventListener("click", downloadSelected);
  elements.deleteSelected.addEventListener("click", openDeleteConfirmation);
  elements.confirmDelete.addEventListener("click", deleteSelected);
  elements.cancelDelete.addEventListener("click", () => elements.deleteDialog.close());
  elements.uploadMarkdown.addEventListener("click", () => elements.uploadInput.click());
  elements.uploadInput.addEventListener("change", () => uploadFiles(elements.uploadInput.files));
  elements.closeOperationResults.addEventListener("click", () => elements.operationResultsDialog.close());
  document.addEventListener("dragover", event => {
    if (!isFileDrag(event)) return;
    event.preventDefault();
    elements.dropOverlay.hidden = false;
  });
  document.addEventListener("dragleave", event => {
    if (event.relatedTarget) return;
    elements.dropOverlay.hidden = true;
  });
  document.addEventListener("drop", event => {
    if (!isFileDrag(event)) return;
    event.preventDefault();
    elements.dropOverlay.hidden = true;
    uploadFiles(event.dataTransfer.files);
  });
  elements.copyViewerContent.addEventListener("click", copyViewerContent);
  elements.downloadViewerContent.addEventListener("click", downloadViewerContent);
  for (const promptLink of elements.promptLinks) {
    promptLink.addEventListener("click", () => viewPrompt(promptLink.dataset.promptKey, promptLink.dataset.promptTitle));
  }
  elements.closeViewer.addEventListener("click", () => elements.viewer.close());
  elements.viewer.addEventListener("close", () => {
    viewerContent = "";
    viewerDownloadUrl = "";
  });

  document.addEventListener("keydown", event => {
    const command = event.ctrlKey || event.metaKey;
    if (!command) return;
    const active = document.activeElement;
    const isTyping = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active?.isContentEditable;
    if (isTyping) return;

    if (event.key.toLowerCase() === "a" && (active === elements.list || elements.list.contains(active))) {
      event.preventDefault();
      for (const item of state.items) {
        if (item.versionKey && !item.isAmbiguous)
          state.selected.set(item.versionKey, item);
      }
      renderList();
      return;
    }

    if (event.key.toLowerCase() === "c" && state.selected.size > 0) {
      const browserSelection = window.getSelection()?.toString();
      if (browserSelection) return;
      event.preventDefault();
      copySelected();
    }
  });

  setInitialTheme();
  load();
})();