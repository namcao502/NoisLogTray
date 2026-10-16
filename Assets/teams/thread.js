// The ticket's Teams view: the conversation and Claude's analysis, the draft post, or a state panel; renders what the host sends, posts the user's actions back.
"use strict";
(() => {
  const params = new URLSearchParams(location.search);
  const post = (message) => window.chrome.webview.postMessage(JSON.stringify(message));

  const setColors = (colors) => {
    for (const [name, value] of Object.entries(colors)) document.documentElement.style.setProperty("--" + name, value);
  };
  const setDark = (dark) => document.documentElement.classList.toggle("dark", dark);

  // Run while the head is parsed, so the first paint already has the app's colors.
  const startColors = params.get("colors");
  if (startColors) setColors(JSON.parse(startColors));
  setDark(params.get("dark") === "1");

  // Ctrl+1..4 switch app pages, as in the terminal (WebView2 keeps keys from the form).
  window.addEventListener("keydown", (e) => {
    if (!e.ctrlKey || e.shiftKey || e.altKey || !/^(Digit|Numpad)[1-4]$/.test(e.code)) return;
    e.preventDefault();
    e.stopPropagation();
    post({ type: "page", digit: Number(e.code.slice(-1)) });
  }, true);

  const Icons = { edit: "\uF3DD", delete: "\uF34C", chevronDown: "\uF2A3", open: "\uF582", refresh: "\uF13D", checkmark: "\uF294" };
  const MaxCachedImageUrls = 60;
  const LoadingImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

  // Every text from the host goes through textContent: names, subjects and the analysis come from other people or Claude.
  const addElement = (parent, tag, className, text) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text) element.textContent = text;
    parent.append(element);
    return element;
  };
  // Functions, not strings, as the replacements: a "$&" in a name stays as it is.
  const format = (template, first, second) => template.replace("{0}", () => first).replace("{1}", () => second || "");

  const start = () => {
    const byId = (id) => document.getElementById(id);
    const channelButton = byId("channelButton");
    const channelText = byId("channelText");
    const channelMenu = byId("channelMenu");
    const channelBox = byId("channelBox");
    const conversationBox = byId("conversationBox");
    const conversationButton = byId("conversationButton");
    const conversationText = byId("conversationText");
    const conversationMenu = byId("conversationMenu");
    const openInTeams = byId("openInTeams");
    const refresh = byId("refresh");
    const status = byId("status");
    const statePanel = byId("statePanel");
    const stateText = byId("stateText");
    const signIn = byId("signIn");
    const retry = byId("retry");
    const threadMode = byId("threadMode");
    const messagesBox = byId("messages");
    const rootPost = byId("rootPost");
    const replies = byId("replies");
    const analysisStrip = byId("analysisStrip");
    const analysisSummary = byId("analysisSummary");
    const analysisToggle = byId("analysisToggle");
    const analysisBody = byId("analysisBody");
    const replySuggestions = byId("replySuggestions");
    const commentCard = byId("commentCard");
    // Built once and kept across renders, so a typed main point and its focus survive a new analysis.
    const suggestionList = addElement(replySuggestions, "div", "");
    suggestionList.style.display = "contents";
    const suggestRow = addElement(replySuggestions, "div", "");
    suggestRow.id = "suggestRow";
    const regenerate = addElement(suggestRow, "button", "btn ghost");
    regenerate.type = "button";
    addElement(regenerate, "span", "glyph", Icons.refresh);
    const regenerateText = addElement(regenerate, "span", "");
    const mainPoint = addElement(suggestRow, "input", "text");
    mainPoint.type = "text";
    mainPoint.spellcheck = false;
    mainPoint.autocomplete = "off";
    let analysisOpen = false;
    const sideColumn = byId("sideColumn");
    const sideHint = byId("sideHint");
    const analysisTitle = byId("analysisTitle");
    const wideLayout = window.matchMedia("(min-width: 1300px)");
    // The same nodes move between the column and the thread card, so a typed prompt and the open state survive a resize.
    const placeSideParts = () => {
      if (wideLayout.matches) sideColumn.append(analysisStrip, commentCard);
      else replySuggestions.before(analysisStrip, commentCard);
    };
    const postMode = byId("postMode");
    const postTitle = byId("postTitle");
    const reloadDraft = byId("reloadDraft");
    const postSource = byId("postSource");
    const postPostedAt = byId("postPostedAt");
    const postSearching = byId("postSearching");
    const postSubject = byId("postSubject");

    // English until the host's "labels" message arrives.
    let labels = {
      openInTeams: "Open in Teams", refresh: "Refresh", channel: "Channel: {0}", pickChannel: "Pick a channel",
      noChannels: "No channels yet - Teams has not been read", signIn: "Sign in to Teams", retry: "Retry",
      image: "Image - open in Teams", edited: "(edited)", edit: "Edit", delete: "Delete", handToClaude: "Hand to Claude", handedToClaude: "Handed to Claude - see the Claude view.",
      commentAsk: "{0} asked to check the PR comments", commentHandOff: "Hand to Claude",
      commentWaiting: "Handed to Claude, waiting for it to finish...", commentOpenThreads: "{0} comments still open",
      commentDone: "Claude is done - the reply is in the suggestions.", commentMarkDone: "Done", dismiss: "Dismiss", deleting: "Deleting...", saving: "Saving...",
      analysisTitle: "Claude's analysis", analysisRunning: "Claude is analyzing...", analysisSummary: "Claude: {0} issues, {1} open",
      noAnalysis: "Claude gave no analysis.", noAnalysisHint: "No analysis yet. Use Analyze above the reply box.", issuesTitle: "Problems raised", statusOpen: "Open", statusAnswered: "Answered",
      statusFixed: "Fixed", diagnosisTitle: "Diagnosis", nextStepTitle: "Next step",
      draftingReplies: "Claude is drafting replies...", regenerate: "Analyze again",
      mainPointPlaceholder: "Main point of the reply (optional)", waitingLine: "{0} is waiting since {1}",
      suggest: "Analyze",
      newPostTitle: "New post", subjectPlaceholder: "Add a title", reloadDraft: "Reload draft",
      reloadDraftTip: "Reload the draft (drops your edits)", postButton: "Post", stillSearching: "Looking for an older post in the channel...",
      bold: "Bold (Ctrl+B)", italic: "Italic (Ctrl+I)", underline: "Underline (Ctrl+U)", strikethrough: "Strikethrough",
      bulletList: "Bulleted list", numberList: "Numbered list", quote: "Quote", link: "Link", code: "Insert code block",
      codeLanguage: "Language", deleteCodeBlock: "Delete block", mention: "Tag someone",
      addImage: "Add an image", send: "Send", save: "Save", cancel: "Cancel", replyHint: "Enter sends, Shift+Enter adds a line",
      postHint: "Ctrl+Enter posts", editHint: "Ctrl+Enter saves, Esc cancels", replyPlaceholder: "Write a reply...", chatPlaceholder: "Message the chat",
      postPlaceholder: "Write the post...", linkPrompt: "Enter a link (http/https)", linkAdd: "Add", linkInvalid: "Only http and https links.",
      removeImage: "Remove image", noOneMatches: "No one matches", emptyReply: "Write the reply first.", emptyMessage: "The message is empty.",
      imageNotSupported: "{0} is not a PNG, JPEG or GIF image.", imageTooLarge: "{0} is over 10 MB.",
      tooManyImages: "At most 6 images per message.", imagesTooLargeInAll: "The images are over 20 MB in all.",
    };
    let mode = null; // "thread", "post", or null while the state panel shows
    let thread = null; // the last "thread" message
    let channels = { groups: [], channelId: null, channelName: null };
    let conversations = null; // the last "conversations" message; null while the ticket has no chat
    let people = []; // [{mri, name, shortName}] from the last "people" message
    let analysis = null; // the last "analysis" message
    let commentFlow = null; // the last "commentFlow" message; stays null until the page handles one
    let commentHandOffError = ""; // the last failed hand-over of the comment card
    let commentHandingOff = false;
    let commentPromptEdit = null; // what the user typed in the card's prompt box, kept across re-renders
    let replySending = false;
    let renderedMessageCount = 0;
    let postDirty = false; // the user changed the draft, so a re-read draft does not replace it
    let appliedDraft = null; // the subject and markdown last put in the post box
    let editing = null; // { messageId, isRoot, element, composer, subjectInput } while one of the user's messages is edited
    let deletingId = null;
    // url -> data URL, null (cannot be shown) or "pending"; outside the render, which runs again on every read.
    const imageCache = new Map();
    // mri -> data URL, null (no picture) or "pending"; lives as long as the page, so a re-read puts the picture back at once.
    const avatarCache = new Map();
    const AvatarDataUrl = /^data:image\/(jpeg|png|gif|webp);base64,/;

    // The picture goes over the letters, which stay underneath for a picture that is gone.
    const setAvatarImage = (avatar, dataUrl) => {
      if (avatar.querySelector("img")) return;
      const picture = document.createElement("img");
      picture.src = dataUrl;
      picture.alt = "";
      avatar.append(picture);
    };

    const showAvatar = (avatar, message) => {
      if (!message.senderMri) return;
      avatar.dataset.mri = message.senderMri;
      if (!avatarCache.has(message.senderMri)) {
        avatarCache.set(message.senderMri, "pending");
        post({ type: "avatar", mri: message.senderMri, name: message.senderName });
      }
      const cached = avatarCache.get(message.senderMri);
      if (typeof cached === "string" && AvatarDataUrl.test(cached)) setAvatarImage(avatar, cached);
    };

    const showLoadedAvatar = (m) => {
      const dataUrl = typeof m.dataUrl === "string" && AvatarDataUrl.test(m.dataUrl) ? m.dataUrl : null;
      avatarCache.set(m.mri, dataUrl);
      if (!dataUrl) return;
      for (const avatar of document.querySelectorAll(".avatar[data-mri]")) {
        if (avatar.dataset.mri === m.mri) setAvatarImage(avatar, dataUrl);
      }
    };

    // An image Teams cannot give us: the same link to the thread that SafeHtml writes for any other image.
    const imagePlaceholder = (url) => {
      const anchor = document.createElement("a");
      anchor.className = "image";
      if (thread && thread.deepLink) anchor.href = thread.deepLink;
      anchor.dataset.src = url;
      anchor.textContent = labels.image;
      return anchor;
    };

    const showAmsImage = (image) => {
      const url = image.dataset.src || "";
      if (!imageCache.has(url)) {
        imageCache.set(url, "pending");
        post({ type: "image", url });
      }
      const cached = imageCache.get(url);
      if (cached === null) image.replaceWith(imagePlaceholder(url));
      else if (cached === "pending") {
        // A blank GIF, not a missing src: Chrome draws a broken-image icon on an img without one.
        image.src = LoadingImage;
        image.classList.add("loading");
      }
      else image.src = cached;
    };

    // Drops the oldest urls that nothing on screen shows; the ones on screen stay, so a re-read never loads them again.
    const trimImageCache = () => {
      if (imageCache.size <= MaxCachedImageUrls) return;
      const shownUrls = new Set([...document.querySelectorAll("[data-src]")].map((element) => element.dataset.src));
      for (const url of imageCache.keys()) {
        if (imageCache.size <= MaxCachedImageUrls) break;
        if (!shownUrls.has(url)) imageCache.delete(url);
      }
    };

    // The thread's images and those of a message being edited, which keep their place in the edit box.
    const showLoadedImage = (m) => {
      const dataUrl = typeof m.dataUrl === "string" && m.dataUrl.startsWith("data:image/") ? m.dataUrl : null;
      imageCache.set(m.url, dataUrl);
      // A thread read to its newest message stays there as its images grow.
      const wasAtBottom = messagesBox.scrollHeight - messagesBox.scrollTop - messagesBox.clientHeight < 2;
      for (const image of document.querySelectorAll("img.ams")) {
        if (image.dataset.src !== m.url) continue;
        if (dataUrl) {
          image.src = dataUrl;
          image.classList.remove("loading");
        }
        else if (image.closest(".image-block")) image.classList.add("missing");
        else image.replaceWith(imagePlaceholder(m.url));
      }
      if (wasAtBottom) messagesBox.scrollTop = messagesBox.scrollHeight;
      trimImageCache();
    };

    const composerOptions = (composerMode, extra) =>
      Object.assign({ mode: composerMode, labels, getPeople: () => people, loadImage: showAmsImage }, extra);

    const replyComposer = window.createComposer(byId("replyBox"), composerOptions("reply", {
      post: (content) => {
        if (replySending) return;
        replySending = true;
        replyComposer.setBusy(true);
        post(Object.assign({ type: "send" }, content));
      },
    }));

    const postComposer = window.createComposer(byId("postBox"), composerOptions("post", {
      onChange: () => { postDirty = true; },
      post: (content) => {
        // With no channel picked, Post only opens the channel list.
        if (!channels.channelName) {
          openChannelMenu();
          return;
        }
        postComposer.setBusy(true);
        post(Object.assign({ type: "post", subject: postSubject.value }, content));
      },
    }));

    const renderChannelButton = () => {
      channelText.textContent = channels.channelName ? format(labels.channel, channels.channelName) : labels.pickChannel;
      channelButton.title = channels.channelName || labels.pickChannel;
    };

    const closeChannelMenu = () => {
      channelMenu.hidden = true;
      channelButton.setAttribute("aria-expanded", "false");
    };

    const pickChannel = (group, item) => {
      closeChannelMenu();
      channels = Object.assign({}, channels, { channelId: item.id, channelName: group.recent ? item.name : group.title + " / " + item.name });
      renderChannelButton();
      post({ type: "selectChannel", channelId: item.id });
    };

    // "Recent" first, then one group per team, the selected channel checked.
    const renderChannelMenu = () => {
      channelMenu.replaceChildren();
      if (channels.groups.length === 0) addElement(channelMenu, "div", "menu-empty", labels.noChannels);
      for (const group of channels.groups) {
        addElement(channelMenu, "div", "menu-title", group.title);
        for (const item of group.channels) {
          const row = addElement(channelMenu, "button", "menu-item");
          row.type = "button";
          row.setAttribute("role", "menuitem");
          addElement(row, "span", "glyph check", item.id === channels.channelId ? Icons.checkmark : "");
          addElement(row, "span", "menu-text", item.name);
          row.addEventListener("click", () => pickChannel(group, item));
        }
      }
    };

    // Only the user's open moves the focus; a read that lands while the menu is open just renders it again.
    function openChannelMenu() {
      renderChannelMenu();
      channelMenu.hidden = false;
      channelButton.setAttribute("aria-expanded", "true");
      const selected = channelMenu.querySelector(".menu-item .check:not(:empty)");
      (selected ? selected.parentElement : channelMenu.querySelector(".menu-item") || channelButton).focus();
    }

    const applyChannels = (m) => {
      channels = { groups: m.channelGroups || [], channelId: m.channelId, channelName: m.channelName };
      renderChannelButton();
      if (!channelMenu.hidden) renderChannelMenu();
    };

    const renderConversationButton = () => {
      const selected = conversations ? conversations.items.find((item) => item.id === conversations.selected) : null;
      conversationText.textContent = selected ? selected.label : "";
      conversationButton.title = conversationText.textContent;
      conversationBox.hidden = conversations === null;
    };

    const closeConversationMenu = () => {
      conversationMenu.hidden = true;
      conversationButton.setAttribute("aria-expanded", "false");
    };

    const pickConversation = (item) => {
      closeConversationMenu();
      conversations = Object.assign({}, conversations, { selected: item.id });
      renderConversationButton();
      post({ type: "selectConversation", chatId: item.id });
    };

    const renderConversationMenu = () => {
      conversationMenu.replaceChildren();
      for (const item of conversations.items) {
        const row = addElement(conversationMenu, "button", "menu-item");
        row.type = "button";
        row.setAttribute("role", "menuitem");
        addElement(row, "span", "glyph check", item.id === conversations.selected ? Icons.checkmark : "");
        addElement(row, "span", "menu-text", item.label);
        row.addEventListener("click", () => pickConversation(item));
      }
    };

    const openConversationMenu = () => {
      renderConversationMenu();
      conversationMenu.hidden = false;
      conversationButton.setAttribute("aria-expanded", "true");
      const selected = conversationMenu.querySelector(".menu-item .check:not(:empty)");
      (selected ? selected.parentElement : conversationMenu.querySelector(".menu-item") || conversationButton).focus();
    };

    const applyConversations = (m) => {
      conversations = m;
      renderConversationButton();
      if (!conversationMenu.hidden) renderConversationMenu();
    };

    const setMode = (newMode) => {
      mode = newMode;
      statePanel.hidden = newMode !== null;
      threadMode.hidden = newMode !== "thread";
      postMode.hidden = newMode !== "post";
      channelBox.hidden = newMode === "thread" && thread !== null && thread.isChat;
      openInTeams.disabled = newMode !== "thread" || !thread || !thread.deepLink;
      if (newMode !== "thread") editing = null;
    };

    const setStatus = (text, kind) => {
      status.textContent = text || "";
      status.className = kind || "";
    };

    // The one HTML sink of this file: TeamsChat.SafeHtml built the html from its own tags and escaped text.
    const renderContent = (body, html) => {
      body.innerHTML = html;
      for (const image of body.querySelectorAll("a.image")) image.textContent = labels.image;
      for (const image of body.querySelectorAll("img.ams")) showAmsImage(image);
      // A Teams code block shows as a card under its language's name.
      for (const pre of body.querySelectorAll('pre[class*="language-"]')) {
        const card = document.createElement("div");
        card.className = "code-card";
        pre.before(card);
        addElement(card, "div", "code-card-head", window.codeBlockLanguageName(pre) || "");
        card.append(pre);
      }
    };

    const cancelEdit = () => {
      editing = null;
      renderThread();
    };

    const saveEdit = (content) => {
      if (!editing) return;
      editing.composer.setBusy(true);
      editing.composer.setStatus(labels.saving, "");
      const subject = editing.isRoot ? editing.subjectInput.value : null;
      post(Object.assign({ type: "edit", messageId: editing.messageId, subject }, content));
    };

    const startEdit = (message, isRoot) => {
      const element = document.createElement("div");
      element.className = "edit-box";
      let subjectInput = null;
      if (isRoot) {
        subjectInput = addElement(element, "input", "text edit-subject");
        subjectInput.type = "text";
        subjectInput.spellcheck = false;
        subjectInput.value = message.subject || "";
        subjectInput.placeholder = labels.subjectPlaceholder;
      }
      const composer = window.createComposer(addElement(element, "div"), composerOptions("edit", { onCancel: cancelEdit, post: saveEdit }));
      if (subjectInput) {
        subjectInput.addEventListener("keydown", (e) => {
          if (e.key === "Escape") {
            e.preventDefault();
            cancelEdit();
          }
          else if (e.key === "Enter" && e.ctrlKey) {
            e.preventDefault();
            composer.submit();
          }
        });
      }
      composer.setContent(message.editHtml || "");
      editing = { messageId: message.id, isRoot, element, composer, subjectInput };
      renderThread();
      composer.focus(false);
    };

    const renderMessage = (container, message, isRoot) => {
      const isEditing = editing !== null && editing.messageId === message.id;
      const row = addElement(container, "div", "msg" + (isRoot ? " root" : "") + (message.mine ? " mine" : ""));
      if (message.id === deletingId) row.classList.add("deleting");
      // Focusable, so the keyboard can reach the action bar that shows on hover or focus.
      if (!isEditing) row.tabIndex = 0;
      showAvatar(addElement(row, "div", "avatar c" + message.colorIndex, message.initials), message);
      const main = addElement(row, "div", "msg-main");
      const head = addElement(main, "div", "head");
      addElement(head, "span", "name", message.senderName);
      addElement(head, "span", "time", message.timeText);
      if (message.edited) addElement(head, "span", "edited", labels.edited);
      if (isEditing) {
        main.append(editing.element);
        return;
      }
      if (message.subject) addElement(main, "div", "subject", message.subject);
      renderContent(addElement(main, "div", message.isPlain ? "body plain" : "body"), message.html);
      if (message.reactions) {
        const reactionRow = addElement(main, "div", "reactions");
        for (const reaction of message.reactions) {
          const chip = addElement(reactionRow, "span", "reaction" + (reaction.mine ? " mine" : ""));
          chip.title = reaction.names || "";
          // Teams' own emoji image; the key stands in for it when the CDN cannot be reached.
          const emoji = document.createElement("img");
          emoji.alt = reaction.key;
          emoji.addEventListener("error", () => emoji.replaceWith(document.createTextNode(reaction.key)), { once: true });
          emoji.src = reaction.url;
          chip.append(emoji);
          addElement(chip, "span", "", String(reaction.count));
        }
      }
      const actions = addElement(row, "div", "actions");
      const addAction = (icon, text, action) => {
        const button = addElement(actions, "button", "btn ghost");
        button.type = "button";
        addElement(button, "span", "glyph", icon);
        addElement(button, "span", "", text);
        button.disabled = deletingId !== null;
        button.addEventListener("click", action);
      };
      if (!message.mine) {
        addAction(Icons.terminal, labels.handToClaude, () => post({ type: "handToClaude", messageId: message.id }));
        return;
      }
      if (message.editable) addAction(Icons.edit, labels.edit, () => startEdit(message, isRoot));
      addAction(Icons.delete, labels.delete, () => post({ type: "delete", messageId: message.id }));
    };

    const addSection = (title) => {
      addElement(analysisBody, "div", "analysis-heading", title);
    };

    const renderAnalysis = () => {
      const hasResult = analysis !== null && analysis.kind !== "none";
      const waiting = mode === "thread" && thread.messages.length !== 0 && thread.needsReply;
      analysisStrip.hidden = mode !== "thread" || (!hasResult && !waiting);
      sideHint.hidden = !analysisStrip.hidden;
      const running = analysis !== null && analysis.kind === "running";
      regenerate.disabled = running;
      regenerateText.textContent = waiting || hasResult ? labels.regenerate : labels.suggest;
      if (analysisStrip.hidden) return;
      analysisBody.replaceChildren();
      const parts = [];
      let failed = false;
      let expandable = false;
      if (running) {
        parts.push(labels.analysisRunning);
      } else if (hasResult && analysis.kind === "failed") {
        parts.push(analysis.error || labels.noAnalysis);
        failed = true;
      } else if (hasResult) {
        const openCount = analysis.issues.filter((issue) => issue.status === "open").length;
        parts.push(analysis.issues.length !== 0
          ? format(labels.analysisSummary, String(analysis.issues.length), String(openCount))
          : labels.analysisTitle);
        expandable = true;
      }
      if (waiting) {
        const lastMessage = thread.messages[thread.messages.length - 1];
        parts.push(format(labels.waitingLine, lastMessage.senderName, lastMessage.timeText));
      }
      analysisSummary.textContent = parts.join("  -  ");
      analysisSummary.classList.toggle("error", failed);
      analysisToggle.hidden = !expandable;
      const bodyOpen = analysisOpen || wideLayout.matches;
      analysisBody.hidden = !expandable || !bodyOpen;
      analysisToggle.setAttribute("aria-expanded", String(expandable && bodyOpen));
      if (!expandable) return;
      const statusLabels = { open: labels.statusOpen, answered: labels.statusAnswered, fixed: labels.statusFixed };
      if (analysis.issues.length !== 0) {
        addSection(labels.issuesTitle);
        for (const issue of analysis.issues) {
          const issueRow = addElement(analysisBody, "div", "issue");
          addElement(issueRow, "span", "issue-dot " + issue.status);
          addElement(issueRow, "span", "issue-text", issue.text);
          addElement(issueRow, "span", "issue-status " + issue.status, statusLabels[issue.status] || statusLabels.open);
        }
      }
      if (analysis.diagnosis) {
        addSection(labels.diagnosisTitle);
        addElement(analysisBody, "div", "analysis-paragraph", analysis.diagnosis);
      }
      if (analysis.nextStep) {
        addSection(labels.nextStepTitle);
        addElement(analysisBody, "div", "analysis-paragraph", analysis.nextStep);
      }
    };

    // The strip above the reply box for a lead's "check comments" ask; every text goes in through textContent.
    const renderCommentCard = () => {
      commentCard.replaceChildren();
      const flow = commentFlow;
      commentCard.hidden = mode !== "thread" || flow === null || !flow.conversationKey || flow.stage === "none";
      if (commentCard.hidden) return;
      const addButton = (text, onClick, primary) => {
        const button = addElement(commentCard, "button", primary ? "btn primary" : "btn", text);
        button.type = "button";
        button.addEventListener("click", onClick);
        return button;
      };
      const dismiss = () => post({ type: "dismissCommentFlow", conversationKey: flow.conversationKey });
      if (flow.stage === "asked") {
        addElement(commentCard, "div", "comment-text", format(labels.commentAsk, flow.sender));
        const promptInput = addElement(commentCard, "input", "text comment-prompt");
        promptInput.type = "text";
        promptInput.value = commentPromptEdit === null ? flow.prompt : commentPromptEdit;
        promptInput.spellcheck = false;
        promptInput.addEventListener("input", () => { commentPromptEdit = promptInput.value; });
        const handOff = addButton(labels.commentHandOff, () => {
          commentHandingOff = true;
          commentHandOffError = "";
          handOff.disabled = true;
          post({ type: "handCommentsToClaude", conversationKey: flow.conversationKey, prompt: promptInput.value });
        }, true);
        handOff.disabled = commentHandingOff;
        addButton(labels.dismiss, dismiss, false);
        if (commentHandOffError) addElement(commentCard, "div", "comment-error", commentHandOffError);
      } else if (flow.stage === "handedOff") {
        const waiting = flow.openThreads > 0
          ? labels.commentWaiting + " " + format(labels.commentOpenThreads, String(flow.openThreads))
          : labels.commentWaiting;
        addElement(commentCard, "div", "comment-text", waiting);
        addButton(labels.commentMarkDone, () => post({ type: "commentFlowDone", conversationKey: flow.conversationKey }), true);
        addButton(labels.dismiss, dismiss, false);
      } else {
        addElement(commentCard, "div", "comment-text", labels.commentDone);
        addButton(labels.dismiss, dismiss, false);
      }
    };

    // The row above the reply box: the comment flow's reply first (when it has one), then the analysis' replies.
    const renderSuggestions = () => {
      suggestionList.replaceChildren();
      const suggestions = [];
      if (commentFlow !== null && commentFlow.reply) {
        suggestions.push({ text: commentFlow.reply, html: commentFlow.replyHtml, first: true });
      }
      if (analysis !== null && analysis.kind === "done") {
        for (const item of analysis.items) suggestions.push({ text: item.text, html: item.html, first: false });
      }
      const drafting = suggestions.length === 0 && analysis !== null && analysis.kind === "running";
      replySuggestions.hidden = mode !== "thread";
      if (drafting) {
        addElement(suggestionList, "span", "faint", labels.draftingReplies);
        return;
      }
      for (const suggestion of suggestions) {
        const button = addElement(suggestionList, "button", suggestion.first ? "suggestion first" : "suggestion", suggestion.text);
        button.type = "button";
        button.title = suggestion.text;
        button.addEventListener("click", () => {
          replyComposer.setContent(suggestion.html);
          replyComposer.focus(false);
        });
      }
    };

    function renderThread() {
      if (!thread) return;
      const scrollTop = messagesBox.scrollTop;
      const messages = thread.messages;
      // An edit whose message is gone (deleted elsewhere) ends with it.
      if (editing && !messages.some((message) => message.id === editing.messageId)) editing = null;
      const focusedInEdit = editing && editing.element.contains(document.activeElement) ? document.activeElement : null;
      rootPost.replaceChildren();
      replies.replaceChildren();
      if (thread.isChat) {
        messages.forEach((message) => renderMessage(replies, message, false));
        replies.hidden = messages.length === 0;
      } else {
        messages.forEach((message, index) => renderMessage(index === 0 ? rootPost : replies, message, index === 0));
        replies.hidden = messages.length < 2;
      }
      replies.classList.toggle("flat", thread.isChat === true);
      replyComposer.setLabels(thread.isChat ? Object.assign({}, labels, { replyPlaceholder: labels.chatPlaceholder }) : labels);
      // A new message scrolls to the newest, as in Teams; a re-read with nothing new keeps the reader's place.
      messagesBox.scrollTop = messages.length !== renderedMessageCount ? messagesBox.scrollHeight : scrollTop;
      renderedMessageCount = messages.length;
      // Moving the edit box dropped its focus; it comes back to the same field.
      if (focusedInEdit && focusedInEdit === editing.subjectInput) focusedInEdit.focus();
      else if (focusedInEdit) editing.composer.focus(false);
      trimImageCache();
      renderAnalysis();
      renderSuggestions();
      renderCommentCard();
    }

    const showThread = (m) => {
      thread = m;
      applyChannels(m);
      setMode("thread");
      setStatus("", "");
      renderThread();
    };

    // An untouched draft follows the host's; one the user changed stays until Reload draft.
    const showDraft = (m) => {
      const isEntering = mode !== "post";
      applyChannels(m);
      setMode("post");
      setStatus("", "");
      postSource.textContent = m.source;
      postPostedAt.textContent = m.postedAt || "";
      postPostedAt.hidden = !m.postedAt;
      postSearching.hidden = !m.stillSearching;
      const draftKey = m.subject + "\n" + m.markdown;
      if (m.reload || (!postDirty && draftKey !== appliedDraft)) {
        postSubject.value = m.subject;
        postComposer.setContent(window.marked.parse(m.markdown, { gfm: true, breaks: true }));
        // A draft that starts at its [BE] / [FE] blocks gets an empty line above them for the description.
        const firstBlock = postComposer.editor.firstElementChild;
        if (firstBlock && firstBlock.textContent.trim().startsWith("[")) {
          const descriptionLine = document.createElement("p");
          descriptionLine.append(document.createElement("br"));
          postComposer.editor.prepend(descriptionLine);
        }
        postComposer.setStatus("", "");
        postDirty = false;
        appliedDraft = draftKey;
        if (m.reload || isEntering) postComposer.focus(true);
      }
    };

    const showState = (m) => {
      // A re-read keeps the thread or the draft on screen; only the status line says it is reading, or that it failed.
      if (m.inline || (m.kind === "loading" && mode !== null)) {
        setStatus(m.text, m.kind === "failed" ? "error" : "");
        return;
      }
      setMode(null);
      setStatus("", "");
      stateText.textContent = m.text;
      stateText.classList.toggle("error", m.kind === "failed");
      signIn.hidden = m.kind !== "signedOut";
      signIn.disabled = false;
      retry.hidden = m.kind !== "failed";
    };

    // The box clears only when this page's own send finishes, not when the host repeats an old "sent".
    const showSendState = (m) => {
      if (m.kind === "sent" && replySending) replyComposer.setContent("");
      replySending = m.kind === "sending";
      replyComposer.setBusy(replySending);
      replyComposer.setStatus(m.text, m.kind === "failed" || m.kind === "uncertain" ? "error" : "");
    };

    const showPostState = (m) => {
      postComposer.setBusy(m.kind === "posting");
      postComposer.setStatus(m.text, m.kind === "warning" ? "warning" : m.kind === "failed" || m.kind === "uncertain" ? "error" : "");
      // Posted: the next draft from the host is taken as it is.
      if (m.kind === "sent") {
        postDirty = false;
        appliedDraft = null;
      }
    };

    const showEditState = (m) => {
      if (!editing || editing.messageId !== m.messageId) return;
      if (m.state === "saved") {
        cancelEdit();
        return;
      }
      editing.composer.setBusy(m.state === "saving");
      editing.composer.setStatus(m.state === "saving" ? labels.saving : m.error, m.state === "saving" ? "" : "error");
    };

    const showDeleteState = (m) => {
      deletingId = m.state === "deleting" ? m.messageId : null;
      if (m.state === "failed" || m.state === "uncertain") setStatus(m.error, "error");
      else if (m.state === "deleting") setStatus(labels.deleting, "");
      else setStatus("", "");
      renderThread();
    };

    const showHandOffState = (m) => {
      if (m.state === "failed") setStatus(m.error, "error");
      else setStatus(labels.handedToClaude, "");
    };

    analysisTitle.textContent = labels.analysisTitle;
    sideHint.textContent = labels.noAnalysisHint;
    placeSideParts();
    wideLayout.addEventListener("change", () => {
      placeSideParts();
      renderAnalysis();
    });

    const applyLabels = (m) => {
      labels = m;
      byId("openInTeamsText").textContent = m.openInTeams;
      byId("refreshText").textContent = m.refresh;
      signIn.textContent = m.signIn;
      retry.textContent = m.retry;
      regenerateText.textContent = m.regenerate;
      analysisTitle.textContent = m.analysisTitle;
      sideHint.textContent = m.noAnalysisHint;
      analysisToggle.title = m.analysisTitle;
      analysisToggle.setAttribute("aria-label", m.analysisTitle);
      mainPoint.placeholder = m.mainPointPlaceholder;
      postTitle.textContent = m.newPostTitle;
      byId("reloadDraftText").textContent = m.reloadDraft;
      reloadDraft.title = m.reloadDraftTip;
      postSearching.textContent = m.stillSearching;
      postSubject.placeholder = m.subjectPlaceholder;
      replyComposer.setLabels(thread && thread.isChat ? Object.assign({}, m, { replyPlaceholder: m.chatPlaceholder }) : m);
      postComposer.setLabels(m);
      renderConversationButton();
      if (!conversationMenu.hidden) renderConversationMenu();
      if (editing) editing.composer.setLabels(m);
      renderChannelButton();
      if (!channelMenu.hidden) renderChannelMenu();
      if (thread && mode === "thread") renderThread();
    };

    // Links in a message never navigate the page; the host opens only http(s) in the browser.
    messagesBox.addEventListener("click", (e) => {
      if (e.target.closest(".edit-box")) return;
      // An image opens its thread in Teams: the app has no image viewer.
      if (e.target.closest("img.ams")) {
        e.preventDefault();
        if (thread && thread.deepLink) post({ type: "openUrl", url: thread.deepLink });
        return;
      }
      const anchor = e.target.closest("a");
      if (!anchor) return;
      e.preventDefault();
      post({ type: "openUrl", url: anchor.getAttribute("href") || "" });
    });
    channelButton.addEventListener("click", () => {
      if (channelMenu.hidden) openChannelMenu();
      else closeChannelMenu();
    });
    channelMenu.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      closeChannelMenu();
      channelButton.focus();
    });
    document.addEventListener("mousedown", (e) => {
      if (!channelMenu.hidden && !channelMenu.contains(e.target) && !channelButton.contains(e.target)) closeChannelMenu();
      if (!conversationMenu.hidden && !conversationMenu.contains(e.target) && !conversationButton.contains(e.target)) closeConversationMenu();
    });
    conversationButton.addEventListener("click", () => {
      if (conversationMenu.hidden) openConversationMenu();
      else closeConversationMenu();
    });
    conversationMenu.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      closeConversationMenu();
      conversationButton.focus();
    });
    openInTeams.addEventListener("click", () => {
      if (thread && thread.deepLink) post({ type: "openUrl", url: thread.deepLink });
    });
    refresh.addEventListener("click", () => post({ type: "refresh" }));
    retry.addEventListener("click", () => post({ type: "refresh" }));
    signIn.addEventListener("click", () => {
      signIn.disabled = true;
      post({ type: "signIn" });
    });
    regenerate.addEventListener("click", () => post({ type: "suggest", mainPoint: mainPoint.value }));
    analysisToggle.addEventListener("click", () => {
      analysisOpen = !analysisOpen;
      renderAnalysis();
    });
    mainPoint.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      e.preventDefault();
      post({ type: "suggest", mainPoint: mainPoint.value });
    });
    reloadDraft.addEventListener("click", () => post({ type: "reloadDraft" }));
    postSubject.addEventListener("input", () => { postDirty = true; });
    postSubject.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && e.ctrlKey) {
        e.preventDefault();
        postComposer.submit();
      }
    });

    window.chrome.webview.addEventListener("message", (event) => {
      const m = JSON.parse(event.data);
      if (m.type === "labels") applyLabels(m);
      else if (m.type === "theme") {
        setColors(m.colors);
        setDark(m.dark);
      }
      else if (m.type === "state") showState(m);
      else if (m.type === "thread") showThread(m);
      else if (m.type === "draft") showDraft(m);
      else if (m.type === "people") people = m.people;
      else if (m.type === "conversations") applyConversations(m);
      else if (m.type === "analysis") {
        analysis = m;
        if (mode === "thread") {
          renderAnalysis();
          renderSuggestions();
        }
      }
      else if (m.type === "sendState") showSendState(m);
      else if (m.type === "postState") showPostState(m);
      else if (m.type === "editState") showEditState(m);
      else if (m.type === "deleteState") showDeleteState(m);
      else if (m.type === "handOffState") showHandOffState(m);
      else if (m.type === "commentFlow") {
        if (commentFlow === null || commentFlow.conversationKey !== m.conversationKey || commentFlow.stage !== m.stage) commentPromptEdit = null;
        commentFlow = m.stage === "none" ? null : m;
        if (mode === "thread") {
          renderCommentCard();
          renderSuggestions();
        }
      }
      else if (m.type === "commentHandOffState") {
        commentHandingOff = false;
        commentHandOffError = m.state === "failed" ? m.error || "" : "";
        if (m.state === "failed") setStatus(m.error, "error");
        renderCommentCard();
      }
      else if (m.type === "image") showLoadedImage(m);
      else if (m.type === "avatar") showLoadedAvatar(m);
    });
    addElement(channelButton, "span", "glyph", Icons.chevronDown);
    addElement(conversationButton, "span", "glyph", Icons.chevronDown);
    addElement(analysisToggle, "span", "glyph", Icons.chevronDown);
    byId("openInTeams").prepend(Object.assign(document.createElement("span"), { className: "glyph", textContent: Icons.open }));
    byId("refresh").prepend(Object.assign(document.createElement("span"), { className: "glyph", textContent: Icons.refresh }));
    renderChannelButton();
    post({ type: "ready" });
  };

  document.addEventListener("DOMContentLoaded", start);
})();
