const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;

export function isVoiceInputSupported() {
  return Boolean(SpeechRecognition);
}

/**
 * @param {HTMLTextAreaElement} textarea
 * @param {{ onListening?: (listening: boolean) => void, onInterim?: (text: string) => void, onError?: (code: string) => void }} callbacks
 */
export function createRuVoiceInput(textarea, callbacks = {}) {
  if (!SpeechRecognition) return null;

  const recognition = new SpeechRecognition();
  recognition.lang = "ru-RU";
  recognition.continuous = true;
  recognition.interimResults = true;

  let listening = false;

  function setListening(next) {
    listening = next;
    callbacks.onListening?.(listening);
  }

  recognition.onresult = (e) => {
    let final = "";
    let interim = "";
    for (let i = e.resultIndex; i < e.results.length; i++) {
      const text = e.results[i][0].transcript;
      if (e.results[i].isFinal) final += text;
      else interim += text;
    }
    if (interim) callbacks.onInterim?.(interim.trim());
    if (final) {
      const trimmed = final.trim();
      if (trimmed) {
        const sep = textarea.value && !textarea.value.endsWith(" ") ? " " : "";
        const max = textarea.maxLength > 0 ? textarea.maxLength : 4000;
        textarea.value = (textarea.value + sep + trimmed).slice(0, max);
        textarea.dispatchEvent(new Event("input", { bubbles: true }));
      }
      callbacks.onInterim?.("");
    }
  };

  recognition.onerror = (e) => {
    callbacks.onError?.(e.error || "unknown");
    setListening(false);
  };

  recognition.onend = () => {
    setListening(false);
  };

  return {
    isListening() {
      return listening;
    },
    toggle() {
      if (listening) {
        recognition.stop();
        setListening(false);
        return;
      }
      try {
        recognition.start();
        setListening(true);
      } catch {
        setListening(false);
        callbacks.onError?.("start-failed");
      }
    },
    stop() {
      if (listening) recognition.stop();
      setListening(false);
      callbacks.onInterim?.("");
    },
  };
}
