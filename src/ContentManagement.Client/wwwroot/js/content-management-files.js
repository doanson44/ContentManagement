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
            if (navigator.clipboard && window.isSecureContext) {
                try {
                    await navigator.clipboard.writeText(text);
                    return true;
                } catch {
                    // Fall through to the legacy selection-based copy for restricted contexts.
                }
            }

            const textarea = document.createElement("textarea");
            textarea.value = text;
            textarea.setAttribute("readonly", "");
            textarea.style.position = "fixed";
            textarea.style.left = "-9999px";
            textarea.style.top = "0";
            document.body.appendChild(textarea);

            try {
                textarea.focus();
                textarea.select();
                textarea.setSelectionRange(0, textarea.value.length);
                return document.execCommand("copy");
            } finally {
                textarea.remove();
            }
        }
    };
})();