window.contentManagementFiles = (() => {
    const subtitleUrls = new Set();
    return {
        createSubtitleUrl: (text) => {
            const url = URL.createObjectURL(new Blob([text], { type: "text/vtt;charset=utf-8" }));
            subtitleUrls.add(url);
            return url;
        },
        revokeSubtitleUrl: (url) => {
            if (url && subtitleUrls.delete(url)) URL.revokeObjectURL(url);
        },
        copyText: async (text) => {
            if (typeof text !== "string" || text.length === 0) return false;

            if (navigator.clipboard && window.isSecureContext) {
                try {
                    await navigator.clipboard.writeText(text);
                    return true;
                } catch {
                    // Clipboard access may be denied by browser permissions.
                }
            }

            let textarea;
            try {
                textarea = document.createElement("textarea");
                textarea.value = text;
                textarea.setAttribute("readonly", "");
                textarea.setAttribute("aria-hidden", "true");
                textarea.style.position = "fixed";
                textarea.style.opacity = "0";
                textarea.style.pointerEvents = "none";
                textarea.style.left = "0";
                textarea.style.top = "0";
                document.body.appendChild(textarea);
                textarea.focus();
                textarea.select();
                textarea.setSelectionRange(0, textarea.value.length);
                return document.execCommand("copy") === true;
            } catch {
                return false;
            } finally {
                textarea?.remove();
            }
        }
    };
})();