## [v0.8.0](https://github.com/DearVa/Everywhere/releases/tag/v0.8.0) - 2026-06-19

### ✨ Features & 🚀 Improvements

- **🔥 Modern UI & Home Page**: Introduced a brand new Home Page and a comprehensive "Modern UI" overhaul, delivering a more elegant and intuitive user experience.
- **🔥 [SKILL.md](https://skill.md) Ecosystem**: Introduced support for `SKILL.md` file definitions. You can now deeply customize and manage agent abilities with ease.
- **🔥 Advanced Prompting**: Added support for the `{DefaultSystemPrompt}` variable with recursive resolution, unlocking more dynamic and powerful prompt templating.
- **🔥 Granular Model Controls**: Introduced per-schema model options and additional advanced parameter settings beyond just `Temperature` and `Top-p`.
- **Software Update Channels**: You can now choose your preferred update channel (e.g., Stable vs. Canary) directly in the settings.
- **AnySearch Integration**: Added `AnySearch` as a supported engine for web browsing tasks.
- **New Models Support**: Added `Claude Opus 4.8`, `Claude Fable 5`, `Gemini 3.5 Flash`, and `MiniMax-M3` to the presets.
- **Enhanced Global Shortcuts**: Added a system tray toggle for the chat window shortcut and introduced support for alternative shortcut keys.
- **UI/UX Performance Leap**: Significantly optimized overall UI performance, delivering a smoother experience for Markdown text selection, rendering, and complex layout elements (like Web Search and MCP buttons).
- Added cloud service quota limit displays and expanded cloud service internationalization.
- The "ask_user_question" tool now includes a free-text field at any time, ensuring that when the assistant asks a question, the user can clearly explain the reason.

### 🐛 Bug Fixes

- Resolved a potential memory leak to ensure better performance and stability during extended background use.
- Fixed an issue where dismissing system notifications (Toasts) could occasionally cause application crashes.
- Fixed a bug where missing characters might appear during Markdown rendering.
- Fixed rendering errors for models returning empty reasoning content.
- Fixed Terminal tool issues where command lines could be truncated and emojis were not displaying properly.
- Fixed an issue where button backgrounds would flicker in Dark Mode.
- Fixed a minor issue where ToDo list items might intermittently fail to display.
- Fixed an issue where chat titles were being truncated too aggressively.
- Addressed various backend file-handling and threading issues to ensure stability.

### 🎉 Contributors
- @sstar16 made their first contribution in #404. Welcome to the project!
- @Fillip74 made a contribution in #403. Thank you!

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.8...v0.8.0



## [v0.7.8](https://github.com/DearVa/Everywhere/releases/tag/v0.7.8) - 2026-05-27

### ⚠️ Important Notice

Due to refactoring of the `Execute In Terminal` tool, the new version may cause some unexpected issues. If you encounter any problems, please don't hesitate to report them to us. We will prioritize fixing critical issues as soon as possible.

### ✨ Features & 🚀 Improvements

- 🔥 Completely refactored the `Execute In Terminal` tool with PTY technology. This achieves comprehensive terminal integration and unified cross-platform behavior. As a result, the Windows installer size is reduced by ~80MB, and the execution stability is significantly improved, laying a solid foundation for features like real-time output display and upcoming command security reviews.
- 🔥 Introduced runtime environment detection to the MCP Manager. If dependencies like `uv` or `Node.js` are missing, Everywhere will notice you with in-app downloading and configuration.
- 🔥 Massively optimized the content truncation mechanism for all tool outputs, enforcing a hard limit of ~40K tokens per tool execution. This prevents "token bombs" from crashing the model context, especially when `Read File` or `Extract Web Content`.
- 🔥 Added the "Continue and Retry" button: When an error interrupts the chat, you can now resume the conversation while preserving the partially generated message from the current turn, instead of starting a new branch.
- 🔥 Added configuration options for `Default Subagent` and `Image Understanding Subagent`. This allows the primary assistant to delegate tasks to lightweight models to save costs, or to bridge the gap if your primary assistant lacks multimodal capabilities.
- Added `GPT-5.5` to the preset mode and removed obsolete Gemini and Kimi models.
- Added auto-download updates in background: When network conditions are ideal and a new version released after 1 day, Everywhere will download updates in the background with speed limits. Intrusive update notifications have been removed.
- Added more advanced options for reasoning parameters (#361).
- Added loading hints while the LLM is generating tool parameters, which is particularly helpful for tools requiring long, complex inputs.
- Optimized window transition and navigation logic for a smoother UX.
- Improved the visual styling and layout of the Chat History drawer.
- Refined the login workflow and fetching logic for official models in Everywhere Cloud, ensuring a more stable connection experience. More user-friendly tooltips were also added.
- Settings for `Temperature` and `Top-p` are now automatically hidden if the currently selected model does not support them.
- Stabilized the logic for handling signatures during LLM calls to reduce unexpected validation errors, and added more detailed error messages to help diagnose signature-related failures.

### 🐛 Bug Fixes

- Fixed an issue where the `Web Search` toggle button remained visible in the chat input area even after tool-calling permissions were explicitly disabled.
- Fixed an issue where API error messages were sometimes not displayed correctly in the UI when using the `OpenAI Responses` schema.
- Fixed an issue where the input box for the `Ask User Question` tool would fail to wrap text under specific circumstances.
- Fixed an issue where sub-agents could incorrectly invoke the `Ask User Question` tool, which previously caused the entire task execution flow to halt unexpectedly.
- Fixed an issue in Light Mode where some code block syntax highlighting colors were difficult to read.
- Fixed minor typos and text description errors in the UI (#373 - Thanks to @Fillip74).

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.7...v0.7.8



## [v0.7.7](https://github.com/DearVa/Everywhere/releases/tag/v0.7.7) - 2026-05-10

### ✨ Features & 🚀 Improvements

![everywhere-cloud-search](https://github.com/user-attachments/assets/563d1349-c0a1-4e37-8e86-b70d16bf0429)

- 🔥 Everywhere Cloud Services now includes an out-of-the-box Web Search API. Existing third-party web search configurations remain unaffected. We also refactored the related entry points and configuration pages to enhance the overall user experience.
- 🔥 Completely refactored the "Web Content Extraction" tool, improving the extraction success rate while significantly reducing token consumption. A dedicated configuration entry has also been added.
- 🔥 Massively optimized the "File System" tool by introducing safety features like timeout protection and context truncation. Prompts and parameters were also refined to ensure more stable tool invocation by LLMs.
- Added an "Allow for this session" option for the Terminal tool, along with an "Auto-approve" toggle in the settings for on-demand use. (*⚠️ Note: Data is invaluable; please be cautious when granting automatic terminal execution permissions!*)
- The chat window can now be resized to a smaller minimum dimension, offering more flexibility when desktop space is limited.
- Removed the dependency on `0Harmony.dll` to prevent false positive detections by antivirus software.
- Added more helpful tooltips and hint texts throughout the application.

### 🐛 Bug Fixes

- Added extra safeguards to ensure the background `Everything` search process is reliably and completely terminated when closing the application.
- Fixed an issue where the cache directory was incorrectly located in some scenarios.
- Fixed an issue where models like MiniMax and Xiaomi Mimo would throw a missing `signature` error when used under the Anthropic schema.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.6...v0.7.7



## [v0.7.6](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.6) - 2026-05-02

### 🐛 Bug Fixes

- Fixed an issue where the main window opened every time when started.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.5...v0.7.6



## [v0.7.5](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.5) - 2026-05-01

### ✨ Features & 🚀 Improvements

![new-history-ui](https://github.com/user-attachments/assets/a4450432-c47d-4da6-8b1e-fe3122a75fb4)

- 🔥 Completely refactored the Chat History page, making it much smoother and more convenient to view, manage, and delete chats.
- 🔥 Completely refactored the "web-snapshot" tool, improving the success rate of webpage visits and the information density of the extracted content.
- Added settings to customize the theme color.
- Optimized the "run-subagent" tool: Sub-agents will now inherit the current visual context and session-level tool approval permissions from the main conversation.
- Enhanced tool execution and bubble interactions: Added more detailed message for tool calls; extended the auto-collapse delay for the "Deep Thinking" bubble; and disabled the auto-collapse behavior for tool execution bubbles to let you easily track the process.
- Improved the visual display of software update push notifications.
- Disabled unnecessary UI animations to reduce performance overhead and improve responsiveness.

### 🐛 Bug Fixes

- Fixed an issue where configurations could not be properly saved or loaded in certain system languages due to decimal separator format differences (e.g., period vs. comma) (#358).
- Fixed an issue where the "manage-todo-list" tool did not correctly update UI.
- Fixed a crash when rendering specific LaTeX formulas.
- **(Windows)** Fixed an issue where the SetText for automation operation will always fail.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.4...v0.7.5



## [v0.7.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.4) - 2026-04-24

### 🐛 Bug Fixes

- 🔥 Fixed an issue where multi-turn conversations would fail when both "Tool Calling" and "Deep Thinking" were enabled simultaneously. This previously caused models like DeepSeek and Kimi to fail functioning properly.
- Fixed an issue where the automatic selection of the "System Assistant" might fail to work in certain situations.
- Fixed an issue where the `items` parameter in the "Manage Todo List" tool was incorrectly set as required, which previously caused the model's initial tool call to fail and led to confusion.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.3...v0.7.4



## [v0.7.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.3) - 2026-04-24

### ✨ Features

- 🔥Added `DeepSeek V4 Pro` and `DeepSeek V4 Flash` to the preset mode, and removed the outdated `DeepSeek Chat` and `DeepSeek Reasoner` models.
- Added an "Disabled" level to the "Reasoning Effort" option in the chat window. For supported models (such as DeepSeek V4, certain Claude models, Xiaomi Mimo, etc.), you can now use this level to completely disable the model's deep thinking process.

### 🐛 Bug Fixes

- 🔥 Fixed an issue where the chat window could not be brought up via shortcuts when the "Automatically Add Element" option was disabled. **Special thanks to all the users who helped us troubleshoot and isolate this issue! (#345, #350, #354)**
- Fixed an issue where the send button remains disabled after selecting a strategy, forcing users to input text before being able to send.
- Fixed an issue where generic strategies failed to display in the list.
- Fixed an issue where the input box was not correctly populated with the original text when editing a user message.
- Fixed an error that occurred when Gemini models attempted to execute parallel tool calls.
- Fixed an issue where auto-generated chat topics would occasionally appear as garbled text under specific circumstances.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.2...v0.7.3



## [v0.7.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.2) - 2026-04-22

### ✨ Features & 🚀 Improvements

- 🔥 Refactored the global hotkey listener logic, which is expected to resolve the issue of occasional hotkey listener failures.
- Optimized chat attachment handling logic: When the selected assistant does not support the current file format, the app now allows adding it with a warning prompt. Upon sending, the assistant will receive the attachment's metadata and be prompted to process it using appropriate tools.
- Optimized the settings loading logic upon application startup: If the configuration file encounters an error, it will now be automatically backed up and preserved. Also added an automatic error fallback mechanism for invalid enum values.
- Added Kimi K2.6 to the list of preset models.
- Improved the error handling logic and error messages during software updates and Everywhere Cloud Services login processes.
- Added handling mechanisms for network request timeouts to prevent potential errors.
- **(Windows)** Added digital signatures to `.dll` files to reduce the risk of false positives from antivirus software.

### 🐛 Bug Fixes

- Fixed an issue where the Xiaomi Mimo model could not correctly enable deep thinking (#282).
- Fixed an issue where the chat would interrupt if the model still attempted to call tools after tool execution permissions were disabled.
- Fixed an issue where the chat would interrupt if the assistant attempted to call a non-existent tool.
- Fixed an issue where the chat would interrupt due to thread contention issues.
- Fixed an issue that caused errors when adding a visual element attachment if the target program unexpectedly exited.
- **(Windows)** Fixed an issue where chat notifications failed to display normally under certain conditions, subsequently causing the chat to interrupt.
- **(macOS)** Fixed an issue where the global hotkey listener failed to work.
- **(macOS)** Fixed an issue where the file selection dialog could not be opened in some situations.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.1...v0.7.2



## [v0.7.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.1) - 2026-04-19

> ⚠️ This update fixes many issues from the 0.7.0 release, including critical crashes. We highly recommend all users to update immediately.

### ✨ Features & 🚀 Improvements

- Added Claude Opus 4.7 and more latest models in the preset mode, and corrected some previously misconfigured parameters.
- Optimized the local file cache by adding proper file extensions for easier local management and inspection.
- Polished text descriptions within the UI and added more quick navigation links.

### 🐛 Bug Fixes

- **🔥 (Windows)**: Fixed a crash occurring on some systems when bringing up the chat window via shortcuts, which was caused by the "Element Fly-in Effect" (#344).
- **🔥 (Windows)** Refactored the visual element selector, completely resolving the issue where using global shortcuts to select elements could cause modifier keys like `Ctrl` or `Alt` to get stuck.
- **🔥 (Windows)**: Fixed an issue where the chat window would disappear immediately when using shortcuts to pick visual elements while the window was unpinned and the "Element Fly-in Effect" was enabled.
- Fixed an issue where images and file attachments could not be pasted directly into the chat input area.
- Fixed an issue where pasted images could not be correctly read by models (#349).
- Fixed an issue where the "Topmost When Typing" and "Pin When Typing" options were not taking effect.
- Fixed an issue where in-app toast notifications would sometimes disappear instantly.
- **(Windows)** Fixed an issue where toggling the chat window's pinned/unpinned state would cause screen flickering, which previously interrupted IME input.
- **(macOS)** Fixed an issue where file attachments could not be properly selected and added within the chat window.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.7.0...v0.7.1



## [v0.7.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.7.0) - 2026-04-15

## 📢 Overview

This release brings you 2 core features: official Cloud Services and Strategy Engine, fully enhancing the out-of-the-box AI experience and convenient automated task processing capabilities. In addition, this update covers up to 19 new features, 27 improvements, and 15 bug fixes, dedicated to creating a smoother, smarter, and more stable user experience.

## ☁️ Cloud Services

In 0.7.0, we have integrated official Cloud Services into Everywhere, providing you with a more out-of-the-box AI experience:

- 🧠 **Out-of-the-box AI Models**: The initial batch of services has integrated large language models from OpenAI, Gemini, Anthropic, and DeepSeek. No API configuration is required; log in to call them directly.
- 💬 **Cross-platform Chat Cloud Sync**: Added cloud sync for chat history, letting your inspiration flow seamlessly across different devices. (Note: Currently, only text message sync is supported; image and file attachment sync will be updated in future versions.)
- 🔑 **BYOK (Bring Your Own Key)**: If you prefer to bring your own key, the addition of cloud services will not affect your local configuration. You can still use it freely as before.

🎉 Click the avatar in the bottom left corner of Everywhere to register/log in. Complete registration now to receive 500,000 credits. For more service and billing details, please visit the [website](https://everywhere.sylinko.com/pricing).

## 🧩 Strategy Engine

Strategy Engine is now live! With the Strategy Engine, Everywhere can intelligently perceive your context and needs, dispatching assistants with one click to resolve complex tasks.

It currently covers various common scenarios such as browsers, text selection, images, and documents. More practical strategies will continue to be expanded in the future.

![strategy-engine](https://github.com/user-attachments/assets/2b45476b-ff8a-4b0f-8df0-1a16a47cc27b)

In future releases, the Strategy Engine will serve as one of Everywhere's core features, gradually rolling out the following capabilities:

- **Custom Strategies**: Users can create and configure personalized strategies according to their needs, defining trigger conditions, prompts, and tool lists via `STRATEGY.md` file to achieve no-code automation.
- **Preprocessors**: Allows preprocessing inputs before strategy execution, such as context parameter extraction and attachment format conversion, to enhance the applicability and effectiveness of strategies.
- **Strategy Workshop**: Allows developers and users to share various custom strategies, aiming to build a rich and diverse strategy ecosystem.

## 🖥️ UI

**Revamped main page navigation design and optimized UX**

![new-navigation](https://github.com/user-attachments/assets/34be93d4-ea85-4791-891c-60b9b6dca737)

---

**Refactored changelog page, allowing you to view the latest release notes before updating**

![new-changelog-page](https://github.com/user-attachments/assets/71b2176d-8739-4fc3-af4b-1f4b5b9d0d1b)

---

**Introduced `ask_user_question` tool**

When handling complex tasks, if the assistant encounters uncertainties, it can now actively pause and ask you questions, waiting for you to confirm details before proceeding.

![ask-user-question-tool](https://github.com/user-attachments/assets/e9daf5e0-a1db-4c73-b3b3-47e0d412dbf6)

---

## ✨ Features

### 🎉 Core Experience

- 🔥 **Concurrent Chats Support**: Now supports running multiple chats simultaneously. Each session processes inputs and outputs independently (#284).
- 🔥 **Custom System Assistant**: Chat title generation is now handled by an independent system assistant, automatically selecting the most suitable model in native and preset modes, with fully customizable configuration.
- Editing a user message during a chat will now create a new branch.
- Added MiniMax (minimaxi.com) to the list of preset model providers.

### 🛠️ Interaction & Workflow

- 🔥 When rejecting a tool call from the assistant, you can now provide a "reason for rejection". This helps the assistant understand your intention and correct its subsequent behavior (#304).
- Added "Always Topmost" and "Topmost When Typing" options to chat window (#295).
- Added global shortcuts for "Pick Visual Element" and "Take Screenshot" (#269).
- Added a "Maximum Context Rounds" option to control context length (#156).

### 💻 UI & Experience

- 🔥 Added a search function to the assistant's avatar editor and expanded more Emoji options.
- Added support for Mermaid diagram rendering (#218).
- When chat window is in the background or minimized, system will show a notification if a conversation is completed, authorization is needed, or an error occurs.
- Introduced brand new dynamic visual feedback when selecting visual elements and building context *(can be disabled in settings)*.
- Added tokens/s statistics (#323).
- Supported using middle mouse button click to quickly delete attachments in the chat input area.
- Added a toggle option for automatic chat title generation.
- When manually checking for updates, error reasons will be prompted if network or server anomalies occur.
- Added a caching mechanism for the MCP plugin tool list.

## 🚀 Improvements

- 🔥 Tool execution and confirmation UI are now directly embedded in chat flow rather than interrupting with dialogs. You can switch chats or abort operations at any time.
- 🔥 The assistant's deep thinking and tool execution bubbles now collapse automatically, and unnecessary "Time Elapsed" displays have been removed, making the chat interface cleaner.
- 🔥 The app now filters out unsupported attachments based on input modalities supported by the current model, preventing model errors caused by unsupported attachments like images (#297, #330).
- Improved the performance and stability of Markdown rendering.
- Added supplementary hint texts and images to some pages.
- Moved the "Temporary Chat" button to the title bar of chat window.
- Improved the editing experience when setting up shortcut keys.
- Each conversation now supports sending up to 50 attachments.
- Optimized the visual context construction algorithm, improving performance when handling long content (#300).
- Added more parameter settings for assistants, such as modalities and context input limits; some parameters have been renamed.
- Temperature and Top-p parameters are now precise to two decimal places (#318).
- Optimized the description text and error messages for Temperature and Top-p parameters.
- Improved the stability of inter-process communication, reducing occasional communication errors.
- When encountering issues, in-chat error reporting will display more detailed error messages to help understand and troubleshoot problems.
- Improved the readability and instructiveness of numerous error messages.
- Improved the stability of Tavily search engine.
- Added an automatic reconnection mechanism for MCP plugins to avoid 4xx errors caused by long-time connections (#232).
- Optimized format compatibility when importing MCP tools from JSON.
- Improved the compatibility of OpenAI Chat Completions.
- Optimized the loading process of system tray icon upon application startup.
- Optimized time parameters in system prompts to increase model cache hit rate.
- (Windows) Added an official digital signature to the installer to enhance security and reduce false positives from antivirus software.
- (Windows) Optimized the textual description for "Startup as Administrator" (#326).
- (macOS) Strengthened the security of system tools to prevent potential command template injection risks.

## 🐛 Bug Fixes

- Fixed an issue where files could not be dragged into the chat window while the assistant was generating a reply.
- Fixed an issue where chat titles sometimes failed to generate automatically.
- Fixed an issue where the chat history list would sometimes freeze, preventing past records from loading.
- Fixed an issue where the chat window could not be brought up by shortcuts in specific scenarios.
- Fixed an occasional crash issue that could occur when deleting chat history.
- Fixed an issue where assistant configurations did not take effect correctly upon startup.
- Fixed an issue where the app would sometimes incorrectly prevent the computer from shutting down normally.
- Fixed an issue where Ollama models did not display deep thinking content.
- Fixed an issue where, in rare cases, tool calls from Gemini models might have parameter errors.
- Fixed an issue where the "Copy Logs to Clipboard" button on the MCP plugin page was broken.
- Fixed an issue where the `reasoning_content` field was not correctly processed for some models.
- (Windows) Fixed an issue where the element selector displayed an offset when multiple monitors were connected.
- (Windows) Fixed an issue where the selected range did not match the actual position when picking visual elements.
- (Windows) Fixed an issue where capturing screen elements could cause the software to crash in certain situations.
- (Windows) Fixed an issue that could trigger an `ERROR_NO_ASSOCIATION` (No associated program found) error when opening or editing settings files (#333).
- Other various minor fixes and stability improvements.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.7...v0.7.0



## [v0.6.7](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.7) - 2026-03-01

### ✨ Features
- **Visual Context Supercharged**: Introducing `get_visual_tree` tool. Your assistant can now call this tool to fetch on-screen elements freely, massively boosting its contextual comprehension and self-awareness capabilities. Note: This is an experimental feature and may not work perfectly with all secenarios. Feedback is highly appreciated!
- **Model Update**: Removed the deprecated Gemini 3 Pro Preview model and added the newly released Gemini 3.1 Pro Preview.

### 🚀 Improvements
- **Framework Upgrade**: Updated the underlying MCP (Model Context Protocol) framework version, which may resolve previously reported edge-case issues (#232).

### 🐛 Bug Fixes
- **Critical Context Fix (Windows)**: Fixed a fatal typo in the visual context extraction algorithm on Windows. This bug previously caused premature truncation of complex contexts, hiding crucial information below the selected element from the assistant.
- Resolved a rendering crash that could occur after enabling the text selection feature (#313).
- Fixed a random crash bug related to Grid layout calculations, bringing further stability to the application.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.6...v0.6.7



## [v0.6.6](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.6) - 2026-02-22

### ✨ Features
- Enhanced the OpenAI schema and deprecated the standalone DeepSeek schema to improve compatibility with dynamic reasoning models.

### 🚀 Improvements
- Optimized the installation process for the built-in Puppeteer.
- Removed "OpenAI" from the UserAgent string to prevent requests from being blocked by Cloudflare WAFs.
- Refreshed the loading animation style for better experience.

### 🐛 Bug Fixes
- Fixed an issue preventing tool calls from functioning correctly with models like Kimi 2.5 under the OpenAI schema.
- Fixed permission validation issues for chat plugins.
- Fixed a bug where sub-agents could recursively call themselves.
- Fixed Anthropic URL parsing; appending `#` to the URL now forces the use of the raw address.
- (macOS) Fixed an issue where the built-in Puppeteer failed to install.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.5...v0.6.6



## [v0.6.5](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.5) - 2026-02-16

### ✨ Features
- Added Claude Opus 4.6 and Haiku 3, setting Sonnet 4.5 as default; Google now defaults to Gemini 3 Flash Preview; DeepSeek now defaults to Deepseek Reasoner; OpenRouter added Kimi 2.5 and Gemini 3 Flash Preview, while upgrading Grok to v4.1.
- Added management for auto-agreeing tool execution; terminal-based tools remain manual for security reasons (#292).
- Added the ability to preview model URLs to improve transparency and user experience.
- (Windows): Improved selection stability, prevented selection results from polluting clipboard history, and resolved the issue where Ctrl+C was sent when used with terminal applications (#281).

### 🚀 Improvements
- Refined error messages for unsupported image inputs and context length limit violations.
- Optimized the loading animations within the chat window.

### 🐛 Bug Fixes
- Fixed the OpenAI.ChangeTrackingList type mismatch error (#171).

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.4...v0.6.5



## [v0.6.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.4) - 2026-02-13

### ✨ Features
- Added reasoning effort settings to the chat window
- Replaced the visual context length limit with predefined tiers for a more user-friendly experience

### 🚀 Improvements
- Improved the logic for scenarios where no custom assistant is selected by adding hints and preventing input loss
- Enhanced telemetry with the inclusion of metrics data

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.3...v0.6.4



## [v0.6.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.3) - 2026-02-11

### ✨ Features
- Added confirmation and undo functionality when deleting chat history
- Added MCP options to the tool menu in the chat window

### 🚀 Improvements
- Optimized the layout of the chat tools page
- Other general UI/UX updates and enhancements

### 🐛 Bug Fixes
- Fixed an issue where DeepSeek reasoning models caused a 400 error when calling tools after outputting body content (#287)

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.2...v0.6.3



## [v0.6.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.2) - 2026-02-10

### ✨ Features
- **Improved Tool Interaction**: Optimized the tool buttons in the chat window to support quick toggling.
- **Enhanced Compatibility**: Connectivity testing and chat title generation now use streaming API calls for better model compatibility.
- **Resource Management**: The Everything plugin on Windows now automatically closes after a period of inactivity.

### 🚀 Improvements
- Removed the inefficient `orderBy` parameter from the `search_files` tool to improve performance.
- Refined various UI text expressions for better clarity and precision.
- Other general UI/UX updates and enhancements.

### 🐛 Bug Fixes
- Fixed an issue on Windows where picking a UI element could trigger an unexpected right-click.
- Fixed a UI synchronization issue where the chat window failed to update after deleting the currently selected custom assistant (#283).
- Fixed incorrect URLs on the Welcome page.
- General stability updates and performance improvements.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.1...v0.6.2



## [v0.6.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.1) - 2026-02-03

### ✨ Features
- **Enhanced Window Pinning**: Introduced three pinning states: Pinned & Topmost, Pinned (Not Topmost), and Unpinned (Auto-hide on lost focus).
- **Model Updates**: Removed obsolete Kimi models and added support for **Kimi K2.5**.
- **Sub-Agent Streaming**: The Sub-Agent tool now supports streaming output.

### 🚀 Improvements
- Optimized environment variable updates; MCP tools now automatically use the latest system environment variables (#264).
- Improved the prompt and logic for automatic chat title generation.
- Tool calling is now enabled by default for new users.

### 🐛 Bug Fixes
- Fixed an issue where the window could be positioned off-screen in multi-monitor setups.
- Fixed an issue where DeepSeek models failed to call tools during the reasoning process.
- Fixed an issue with incorrect token usage counting.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.6.0...v0.6.1



## [v0.6.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.6.0) - 2026-01-21

### ✨ Features
- 🎉 **macOS Support**: Native macOS support is here! We have brought a complete experience almost identical to the Windows version.
- Added "Selection Context" (Experimental): When enabled in settings, the selected text and its context will be automatically attached, providing better context understanding for translation and explanation tasks.
- Added a button to open the Settings window directly from the Chat Window.

### 🚀 Improvements
- Improved encoding detection accuracy for the file reader tool.
- Optimized prompts for the web search tool.

### 🐛 Bug Fixes
- Fixed broken support for DeepSeek reasoning models.
- Fixed an issue where token usage statistics for Claude models were displayed incorrectly.
- Fixed an issue where files could not be pasted correctly if the file path contained spaces.
- Fixed an issue where the color picker in the assistant icon editor was unresponsive.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.11...v0.6.0



## [v0.5.11](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.11) - 2026-01-12

### 🐛 Bug Fixes
- Model provider combobox is empty in preset mode (#254)

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.10...v0.5.11



## [v0.5.10](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.10) - 2026-01-12

### 🐛 Bug Fixes
- (Critical) Fixed an issue where Everywhere cannot get details/contents of UI elements.
- (Windows) Fixed an issue where API keys cannot be configured when startup as administrator.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.9...v0.5.10



## [v0.5.9](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.9) - 2026-01-10

### 🐛 Bug Fixes
- Fixed an issue where the display order of content in the ApiKey selection box was incorrect.
- (Windows) Fixed an issue where custom assistants were not migrating correctly.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.8...v0.5.9



## [v0.5.8](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.8) - 2026-01-08

### ✨ Features
- Added a screenshot tool for quickly attaching screen captures in the chat window.
- Added support for the UniFuncs search engine.

### 🔄️ Changed
- Removed the unstable UI element manipulation tool.

### 🐛 Bug Fixes
- Fixed a critical issue where assistant preset modes were not correctly applied.
- Fixed an issue where assistants could not be duplicated.
- Fixed styling issues on the Welcome Page.
- Fixed an issue where the update check timestamp did not use the local time zone.
- (Windows) Fixed an issue where the PowerShell plugin failed to refresh environment variables.
- (Windows) Fixed an issue where the main window could incorrectly remain always-on-top.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.7...v0.5.8



## [v0.5.7](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.7) - 2026-01-06

⚠️ IMPORTANT WARNING
If upgrading from v0.5.5 or earlier, this version will automatically migrate your settings file. It is recommended to backup `C:\Users\[Username]\AppData\Roaming\Everywhere\settings.json` beforehand. Migration completes upon startup. All existing API keys will be cleared and moved to the `LegacyApiKeys` property in `settings.json`. You will need to reconfigure them. For security reasons, please delete `LegacyApiKeys` immediately after reconfiguring your keys.

### ✨ Features
- Improved tray icon interaction: Single-click to open the Chat Window, double-click to open the Main Window.

### 🐛 Bug Fixes
- (Important) Fixed an issue where "Advanced Configuration" settings were not applied correctly.
- Fixed an issue where the Main Window could not be restored from the tray menu if it was minimized.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.6...v0.5.7



## [v0.5.6](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.6) - 2026-01-05

### ⚠️ Important Changes
**Security Update**: API keys are now stored using a more secure encryption method.

**IMPORTANT WARNING**: This version will automatically migrate your settings file. It is recommended to backup `C:\Users\[Username]\AppData\Roaming\Everywhere\settings.json` beforehand. Migration completes upon startup. All existing API keys will be cleared and moved to the `LegacyApiKeys` property in `settings.json`. You will need to reconfigure them. For security reasons, please delete `LegacyApiKeys` immediately after reconfiguring your keys.

### ✨ Features
- **New "Essential" Plugin**: Adds support for running Sub-Agents and managing Todo lists, enhancing LLM performance on complex tasks.
- **Refactored Custom Assistant & Welcome Page**: Now features "Preset Mode" and "Advanced Mode" for a more user-friendly experience.
- Added the ability to import MCP servers from JSON (#191).
- Added a setting to automatically create a new chat every time the chat window is opened (#32).

### 🚀 Improvements
- Assistants now reliably use the correct working directory instead of defaulting to `C:\Windows\System32` (#161).
- Optimized the default system prompt.
- Improved the UI for checking updates.
- Various other UI/UX improvements.

### 🐛 Bug Fixes
- Fixed a critical issue where file attachments could cause an infinite loop and memory exhaustion crash.
- Fixed an issue where Gemini 3 might fail when calling tools.
- Fixed an issue where the File System plugin returned incomplete paths during file search.
- Fixed an issue where the PowerShell plugin failed to capture Warning output (#225).
- Fixed encoding issues in the PowerShell plugin.
- Fixed an issue where LaTeX formulas were not clearly visible in Light Mode.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.5...v0.5.6



## [v0.5.5](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.5) - 2025-12-21

### 🚀 Improvements
- Optimized various UI visual details.

### 🐛 Bug Fixes
- Fixed an issue where the "Everywhere minimized to tray" notification appeared every time.
- Fixed an issue where the enabled state of MCP tools failed to load correctly.
- Fixed an issue where the tool permission consent dialog failed to display.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.4...v0.5.5



## [v0.5.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.4) - 2025-12-20

### ⚠️ Important Changes
- Changed the software license from Apache 2.0 to **BSL 1.1**.

### ✨ Features
- 🎨 Brand New UI: 
  - A complete refactor of the user interface, featuring a modern design, responsive layout, and a fresh chat window style with Light Mode support. The app now automatically syncs with the system accent color and includes more helpful tooltips.
- Added support for the OpenAI Responses API.
- Added shortcuts for quick navigation:
  - Scroll on the assistant icon or use `Ctrl` + `0-9` keys to switch assistants.
  - Press `Ctrl` + `H` to toggle the chat history view.

### 🚀 Improvements
- Window position and size (for both Settings and Chat) are now automatically saved and restored.
- Improved the visual style of the element selector popup.
- Streamlined the MCP configuration process.
- Font size settings now correctly apply to the chat window.

### 🐛 Bug Fixes
- Fixed an issue where the element selector was offset on multi-monitor setups with different scaling factors (#17).
- Fixed an issue where the screenshot tool could freeze (#177).
- Fixed an issue where Anthropic API calls would fail (#198).
- Fixed an issue where DeepSeek models encountered errors when using tools during the reasoning process (#208).
- Fixed a potential memory leak (#207).

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.3...v0.5.4



## [v0.5.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.3) - 2025-12-7

### ✨ Features
- Added a debug option in settings to create a process dump.

### 🚀 Improvements
- Improved the UI display for reasoning output.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.2...v0.5.3



## [v0.5.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.2) - 2025-12-3

Thanks to [@AidenNovak](https://github.com/AidenNovak) for sponsoring the Apple Developer Program fee. The Mac version is in its final stages of development and will be released soon.

### ✨ Features
- Added support for the Gemini 3 Pro protocol.
- Added the ability to export as Markdown (#180).
- Custom assistants now support drag-and-drop sorting.

### 🐛 Bug Fixes
- Fixed an issue where Google search was not working.
- Fixed an issue where data was lost when copying custom assistants.
- Fixed an issue with abnormal background CPU usage.
- Fixed an issue where Gemini could not call tools (#174).
- Fixed an issue where the settings button in the chat window incorrectly received focus (#173).
- Fixed an issue where rendered Markdown checkboxes were too large (#172).
- Fixed an issue where parameter types were incorrectly converted to strings during MCP calls (#167).
- Fixed a missing reset button in the model settings template (#149, thanks to @ChuheLin).

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.1...v0.5.2



## [v0.5.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.1) - 2025-11-24

### ✨ Features
- Added a digital signature to the software (Thanks to Certum).
- Desktop notifications will now be displayed for permission consent when the chat window is hidden.

### 🚀 Improvements
- Enabled trimming optimization, reducing the application size by approximately 50%.
- Optimized the terminal plugin.

### 🐛 Bug Fixes
- Fixed an issue where some MCP plugins continued to run in the background after the application was closed.
- Fixed an issue where settings were sometimes not saved correctly.
- Fixed an issue where terminal execution output was sometimes not displayed.
- Fixed an issue where some plugin icons were not displayed.
- Fixed an issue where the chat window could not be closed using the shortcut key.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.5.0...v0.5.1



## [v0.5.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.5.0) - 2025-11-22

### ✨ Features
- 🎉 **Major Update: Added support for MCP (Model Context Protocol)!** You can now add your own MCP tools, which will be automatically enabled when called by an assistant. Supports Stdio, Streamable HTTP, and SSE protocols.

### 🚀 Improvements
- When a tool called by an assistant is not found, it now performs a fuzzy match and informs the assistant, reducing model hallucinations.
- (Windows) Added output display for `Everything` plugin.
- (Windows) Removed `Windows System API` plugin.
- Other UI adjustments and bug fixes.

### 🐛 Bug Fixes
- Fixed an issue where custom assistant avatars were stretched (#155).
- Fixed an issue where Markdown content was difficult to select and could cause errors when copying (#91).
- Fixed an issue where bold or italic text in Markdown would revert to normal font when selected (#114).
- Fixed a bug where capturing a UI element could not be canceled.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.7...v0.5.0



## [v0.4.7](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.7) - 2025-11-19

### ✨ Features
- Added the ability to copy and edit sent messages. You can also hold `Shift` while clicking the copy button to get the raw message content (#70).
- Added a setting to adjust the font size (#47).
- Added a button to duplicate custom assistants (#150).
- Added support for Gemini 3 Pro Preview.

### 🚀 Improvements
- Optimized window positioning and resizing behavior. The chat window now opens centered and remembers its last position. The window size is no longer reset when switching chats unless manually minimized.

### 🐛 Bug Fixes
- Fixed an issue where adding an attachment could result in an incorrect file extension.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.6...v0.4.7



## [v0.4.6](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.6) - 2025-11-15

### ✨ Features
- Added native support for the Gemini schema, which fixes the issue where Gemini could not upload images (#125).
- Added drag-and-drop support for files and text (#135).

### 🚀 Improvements
- Improved error message display (e.g., context length exceeded, network proxy errors, and duplicated error messages).
- Improved UI appearance and visual effects.

### 🐛 Bug Fixes
- Fixed an issue where the log folder could not be opened if the path contained non-ASCII characters (such as Chinese) (#117).
- Fixed some UI elements not responding correctly when switching languages.
- Fixed garbled UI text for English users in some cases.
- (Windows) Fixed an issue where terminal execution could sometimes not be terminated, causing the application to freeze.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.5...v0.4.6



## [v0.4.5](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.5) - 2025-11-13

### ✨ Features
- Added a setting for model request timeout.
- Added shortcuts in settings to "Edit configuration file" and "Open log folder" (Thanks @TheNotoBarth).
- Added rendering support for LaTeX formulas.

### 🚀 Improvements
- Network proxy settings now take effect immediately without requiring an application restart.
- Improved error messages for large language models (Thanks @TheNotoBarth).
- Optimized the loading performance of the settings page.
- Removed the unsupported `x-ai/grok-4-fast:free` model from OpenRouter.
- Improved the display of some error messages.

### 🐛 Bug Fixes
- Refactored the underlying data model to fix a bug where messages could be displayed repeatedly.
- Fixed a potential memory leak.
- Optimized the display logic for dialog boxes to prevent them from going beyond the window boundaries and becoming inoperable.
- Fixed an issue where model requests could not handle redirects automatically.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.4...v0.4.5



## [v0.4.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.4) - 2025-11-5

- 📢 The macOS version is on the way and is expected to be released in v0.5.0. Good things take time, so please be patient!

### ✨ Features
- Added network proxy settings, which can be configured manually. **A restart is required for changes to take effect!**
- Added language support for Spanish, Russian, French, Italian, Japanese, Korean, Turkish, and Traditional Chinese (translated by GPT-4o).

### 🚀 Improvements
- Optimized chat plugins:
  - Chat plugin features now display user-friendly descriptions.
  - PowerShell now shows detailed explanations, specific commands, and the final output.
  - For security reasons, the "Allow for this session" and "Always allow" options are not available for multi-line PowerShell script execution.

### 🐛 Bug Fixes
- Fixed an issue where PowerShell execution could not be interrupted (#104).

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.3...v0.4.4



## [v0.4.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.3) - 2025-11-2

### ✨ Features
- Added Türkçe language support (Thanks @complex-cgn)
- Improved chat history viewing & management (including topic editing and multi-selecting/deleting chats)

### 🚀 Improvements
- Reduced memory usage & UI freeze when rendering markdown codeblocks (2700% improvement)
- Added more icons to the assistant IconEditor and optimized its performance

### 🐛 Bug Fixes
- (Windows) Fixed an issue where Everywhere could prevent system shutdown
- Fixed an issue where the chat window could not be closed by pressing the Esc key
- Fixed an issue where canceling a tool call could prevent the conversation from continuing
- Fixed an issue where some reasoning-focused LLMs could not use tools correctly

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.2...v0.4.3



## [v0.4.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.2) - 2025-10-28

### ✨ Features
- Now you can upload documents (PDF, Word, Text, etc.) directly in the chat window as attachments for context (⚠️ only supported by models that allow file inputs)

### 🚀 Improvements
- Chat window can be closed when press shortcut again
- Optimized encoding handling in "File system" chat plugin

### 🐛 Bug Fixes
- Fixed chat topic may not generate correctly for some models
- Fixed "Web search" chat tool may report `count out of range` error
- Fixed "Scroll to end" button in chat window mistakenly get focused
- (Windows) Fixed missing `Everything.dll`
- (Windows) Fixed chat window still appears in `Alt+Tab` list when closed
- (Windows) Fixed chat window disappears when picking a file
- (Windows) Fixed icon & title of update notify is missing

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.1...v0.4.2



## [v0.4.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.1) - 2025-10-27

### ⚠️ BREAKING CHANGE: Chat window shortcut will reset to `Ctrl+Shift+E` due to renaming "Hotkey" to "Shortcut".
️
### 🚀 Improvements
- Renamed DeepSeek models to their new official names
- I18N: Changed "Hotkey" to "Shortcut"
- Refactored "operate UI elements" chat tool for better stability

### 🐛 Bug Fixes
- Fixed window cannot maximize when clicking the maximize button
- Fixed "Web snapshot" chat tool not working
- Fixed "everything" chat tool cannot work when "file system" chat tool is enabled
- Fixed token counting may be bigger than actual usage in some cases

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.4.0...v0.4.1



## [v0.4.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.4.0) - 2025-10-26

### ✨ Features
- Plugin Execution Feedback
  - High-permission plugins now require user confirmation before running
  - Results of plugin calls are now displayed in the chat window 
    - File system plugin lists which files were accessed
    - File changes are shown and can be reviewed before applying
    - Web search now displays the specific query being used
- Temporary Chat
  - Temporary chats that are not saved will now be automatically deleted when switching to another chat or creating a new one
  - You can choose to automatically enter temporary chat mode in settings
- Web Search Enhancements
  - Added Jina as a web search provider
  - Added SearXNG as a web search provider
- Added settings for controlling visual context usage and detail level
- Chat window now displays the current chat title
- (Windows) Integrated Everything to accelerate local file searches

### 🚀 Improvements
- Improved the main window UI layout & style
- Enabled right-click context menu (copy, cut, paste) in the chat input box
- Added a scroll-to-bottom button in the chat window
- Added more emoji choices for custom assistants

### 🐛 Bug Fixes
- Fixed chat history was sometimes not sorted correctly
- Fixed the `Alt` key could not be used as a hotkey
- Fixed the default assistant's icon could not be changed
- Fixed the element picker could not be closed with a right-click
- Fixed the main window would appear after picking an element
- Fixed potential unresponsiveness issues during chats
- Fixed connection test failures for Custom Assistants
- Fixed chat title generation may fail for some models
- Fixed fonts may become _Italic_ unexpectedly
- Fixed a dead link in the Welcome Dialog
- Fixed a recursive self-reference issue
- Fixed wrong acrylic effect on Windows 10

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.12...v0.4.0



## [v0.3.12](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.12) - 2025-10-16

### 🚀 Improvements
- Removed the obsolete Bing web search engine
- Optimized error handling

### 🐛 Bug Fixes
- Fixed an issue where the chat window could not be resized
- Fixed an issue where the Tavily search engine could not be invoked
- Fixed an issue where the chat action bubble did not display error messages
- Fixed an issue where variables in the system prompt were not rendered
- Fixed an issue where the chat topic summary was sometimes empty (Note: This is not fully resolved, as some models may still produce empty results)

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.11...v0.3.12



## [v0.3.11](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.11) - 2025-10-16

### ⚠️ Breaking Changes ⚠️
Due to the model configuration page being rebuilt, previously configured model settings (including API keys, etc.) will be lost! However, they still exist in the software settings file. Advanced users can find them at `C:\Users\<username>\AppData\Roaming\Everywhere\settings.json`.

### ✨ Features
- 🎉 Added custom assistants! You can now create multiple assistants with different icons, names, and prompts, and switch between them freely during a chat
- Added support for the Tavily web search engine

### 🚀 Improvements
- Optimized exception handling

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.10...v0.3.11



## [v0.3.10](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.10) - 2025-10-14

### 🚀 Improvements
- Introduced a new, modern installer that remembers the previous installation location during updates

### 🐛 Bug Fixes
- Fixed an issue where an error was thrown if the OpenAI API key was empty (which is allowed for services like LM Studio)
- Fixed a bug that prevented pasting images as attachments in some cases
- Fixed a bug that caused the application to freeze when sending messages with images
- Fixed an issue causing an HTTP 400 error during function calls
- Fixed an issue where requests could be blocked by Cloudflare from some third-party model providers

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.9...v0.3.10



## [v0.3.9](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.9) - 2025-10-13

### ✨ Features
- Provider icons in settings are now loaded as local resources for faster display
- Added deep-thought output support for Ollama, SiliconFlow, and some OpenAI-compatible models; fixed SiliconFlow and similar models not outputting results
- Added option to show chat plugin permissions in settings

### 🚀 Improvements
- Enhanced error handling and user-friendly messages

### 🐛 Bug Fixes
- Fixed dialog covering the title bar, making the window undraggable or unresponsive
- Fixed some prompt tasks (e.g. translation) may use the wrong target language

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.8...v0.3.9



## [v0.3.8](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.8) - 2025-10-11

### ✨ Features
- Software updates can now be cancelled by dismissing the toast notification
- Added more keyboard shortcuts: `Ctrl+N` for a new chat, `Ctrl+T` to for tools switch
- Added a visual tree length limit setting to save tokens
- Added a notification when an update is available

### 🚀 Improvements
- Optimized the button layout in the chat window
- Added more friendly error messages for a better user experience

### 🐛 Bug Fixes
- Fixed a potential error when loading settings
- Fixed an issue where the chat window could not be reopened after being accidentally closed
- Fixed a missing scrollbar on the chat plugin page (#28)
- Fixed unnecessary telemetry logging
- Corrected a typo for an Ollama model: deepseek R1 8B -> deepseek R1 7B

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.7...v0.3.8



## [v0.3.7](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.7) - 2025-10-11

### 🐞 Fixed
- Fixed error messages were incorrectly parsed as "unknown".

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.6...v0.3.7



## [v0.3.6](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.6) - 2025-10-10

### ✨ New Features
- Added chat statistics in the chat window, which can be toggled in settings.
- Added a setting to control whether to automatically attach the focused element when opening the chat window.
- Added a setting to allow the model to continue generating responses in the background after the chat window is closed.
- Added support for `Claude Sonnet 4.5`.

### 🔄️ Changed
- Improved tooltips for plugin settings.
- Most error messages are now translated and provide more detailed hints.
- Improved the download speed and stability of in-app updates.
- Model parameter settings are now expanded by default to prevent them from being missed.

### 🐞 Fixed
- Fixed an issue where the model's tool-call usage was displayed in the wrong position.
- Fixed an issue where the chat window could not be reopened after being closed while a message was being streamed.
- Fixed an issue where the `Shift` and `Win` keys could become unresponsive if a hotkey included the `Win` key. You can now set the Copilot key as a hotkey normally.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.5...v0.3.6



## [v0.3.5](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.5) - 2025-10-09

### 🐞 Fixed
- Fixed hotkey input box crashes when clicking twice [#20](https://github.com/Sylinko/Everywhere/issues/20)
- Fixed potential null pointer error when sending message
- Fixed wrong telemetry log level

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.4...v0.3.5



## [v0.3.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.4) - 2025-10-09

### 🔄️ Changed
- Improved user prompt for tool usage
- Improved settings saving & loading logic
- Added logging for telemetry
- Removed unnecessary telemetry data

### 🐞 Fixed
- Fixed chat title generation for non-OpenAI models will fail
- Fixed web search plugin may not work in some cases
- Fixed custom model not saved or applied
- Fixed visual tree plugin is not disabled correctly

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.3...v0.3.4



## [v0.3.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.3) - 2025-10-08

### ✨ New Features
- Added telemetry to help us improve. See [Data and Privacy](https://github.com/Sylinko/Everywhere/blob/main/DATA_AND_PRIVACY.md)
- Unsent messages will be saved automatically

### 🔄️ Changed
- Improved sidebar UI and animation

### 🐞 Fixed
- Fixed update message in settings page may disappear when fetching new version

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.2...v0.3.3



## [v0.3.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.2) - 2025-10-05

### 🐞 Fixed
- Fixed chat input box watermark behavior error
- (Windows) Fixed powershell plugin missing modules

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.1...v0.3.2



## [v0.3.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.1) - 2025-10-04

### 🔄️ Changed
- Improved markdown rendering styles
- Improved OOBE experience
- Changed official website link to https://everywhere.sylinko.com/

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.3.0...v0.3.1



## [v0.3.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.3.0) - 2025-09-24

### ✨ New Features
- 🎉 New Icon
- Added acrylic effect to tray icon menu
- Added OOBE (Out-Of-Box Experience) for first time users, including:
  - The welcome Dialog
  - Quick Setup Wizard
- Added support for custom model
- Added chat attachments storage
- Added support for more hotkeys, such as `Copilot` key on Windows
- Added watchdog process
- Chat window can be resized manually
- Chat window will show in taskbar when pinned

### 🔄️ Changed
- Refactored Plugin System, including:
  - Added Plugin Manager in Settings
  - Added file system plugin for reading and writing files
  - Added code execution plugin with PowerShell on Windows
  - Added web browsing plugin with Puppeteer
  - Added visual element plugin for capturing screen content when UI automation is not available
  - Refactored web search plugin
- Refactored logging system with structured logging
- Improved visual capturing performance
- Improved acrylic effect visibility

### 🐞 Fixed
- Fixed removing or switching chat history frequently may cause crash
- Fixed emoji rendering issues in the chat window
- Fixed application may freeze when active chat window in some cases
- Fixed settings load/save issues
- Fixed new chat button disable state is not updated when switching chat history
- Fixed detecting focused element mistakenly in some cases
- Fixed chat window may auto scroll when selecting text

### ⚠️ Known Issues
- Chat messages may disappear when selecting text
- Chat window may flicker when pinned

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.2.4...v0.3.0



## [v0.2.4](https://github.com/Sylinko/Everywhere/releases/tag/v0.2.4) - 2025-08-15

### ✨ New Features
- Added Change Log in Welcome Dialog

### 🔄️ Changed
- Apply warning level filter to EF Core logging

### 🐞 Fixed
- Fixed Google Gemini invoking issues
- Fixed Restart as Administrator may not work on some cases
- Fixed Dialog and Toast may crash the app when reopen after closed a window
- Fixed `ChatElementAttachment`'s overlay window may cover the `ChatWindow`
- Fixed `ChatElementAttachment`'s overlay window may not disappear

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.2.3...v0.2.4



## [v0.2.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.2.3) - 2025-08-14

### ✨ New Features
- Added settings for automatically startup
- Added settings for Software Update

### 🐞 Fixed
- Fixed markdown rendering issues in the Chat Window

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.2.2...v0.2.3



## [v0.2.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.2.2) - 2025-08-11

### ✨ New Features
- **Model Support**: Added support for `Claude Opus 4.1`

### 🔄️ Changed
- Split settings into separate sidebar items

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.2.1...v0.2.2



## [v0.2.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.2.1) - 2025-08-11

### ✨ New Features
- **Model Support**: Added support for `GPT-5` series models:
  - `GPT-5`
  - `GPT-5 mini`
  - `GPT-5 nano`

### 🐞 Fixed
- Fixed markdown rendering issues in the Chat Window

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.2.0...v0.2.1



## [v0.2.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.2.0) - 2025-08-10

This update introduces support for over 20 new models and a completely refactored settings page for a better user experience.

### ✨ New Features

We've integrated the following new models:

- **OpenAI**: `o4-mini`, `o3`, `GPT-4.1`, `GPT-4.1 mini`, `GPT-4o` (`GPT-5` series will be released in next version)
- **Anthropic**: `Claude Opus 4`, `Claude Sonnet 4`, `Claude 3.7 Sonnet`, `Claude 3.5 Haiku`
- **Google**: `Gemini 2.5 Pro`, `Gemini 2.5 Flash`, `Gemini 2.5 Flash-Lite`
- **DeepSeek**: `DeepSeek V3`, `DeepSeek R1`
- **Moonshot**: `Kimi K2`, `Kimi Latest`, `Kimi Thinking Preview`
- **xAI**: `Grok 4`, `Grok 3 Mini`, `Grok 3`
- **Ollama**: `GPT-OSS 20B`, `DeekSeek R1 7B`, `Qwen 3 8B`

### ⚠️ BREAKING CHANGE: Database Refactor

To improve performance and stability, the chat database has been refactored.

- **As this is a beta release, chat history from previous versions is no longer available.**
- The new database structure now supports data migrations, which will prevent data loss in future updates. We appreciate your understanding.

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.1.3...v0.2.0



## [v0.1.3](https://github.com/Sylinko/Everywhere/releases/tag/v0.1.3) - 2025-08-08

### ✨ New Features
- Added a pin button to the Chat Window, to keep it always on top and not close on lost focus
- Added detailed error messages in the Chat Window
- Added auto enum settings support by @SlimeNull in #10

### 🐞 Fixed
- Fixed ChatInputBox max height

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.1.2...v0.1.3



## [v0.1.2](https://github.com/Sylinko/Everywhere/releases/tag/v0.1.2) - 2025-08-02

### ✨ New Features
- Added a notification when the app is first hide to the system tray

### 🔄️ Changed
- (Style) Decreased the background opacity of the main window, for Mica effect

### 🐞 Fixed
- Fixed wrong links in Welcome Dialog

### ⚠️ Known Issues
- The opacity of tray icon menu is broken

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.1.1...v0.1.2



## [v0.1.1](https://github.com/Sylinko/Everywhere/releases/tag/v0.1.1) - 2025-07-31

### ✨ New Features
- Added Logging

### 🗑️ Removed
- Removed custom window corner radius (Too many bugs, not worth it)

### 🐞 Fixed
- Fixed I18N not working when Language is not set

**Full Changelog**: https://github.com/Sylinko/Everywhere/compare/v0.1.0...v0.1.1



## [v0.1.0](https://github.com/Sylinko/Everywhere/releases/tag/v0.1.0) - 2025-07-31

### First Release · 万物生于有，有生于无。
