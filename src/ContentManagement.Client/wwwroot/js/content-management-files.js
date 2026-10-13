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
        getSelectedFileMetadata: (input) => {
            const file = input?.files?.[0];
            return file ? { name: file.name, size: file.size, contentType: file.type || "application/octet-stream" } : null;
        },
        uploadFile: (input, url, dotNetReference) => new Promise((resolve, reject) => {
            const file = input?.files?.[0];
            if (!file) {
                reject(new Error("No file selected."));
                return;
            }

            const form = new FormData();
            form.append("file", file, file.name);
            const xhr = new XMLHttpRequest();
            xhr.open("POST", url, true);
            xhr.withCredentials = true;
            xhr.upload.addEventListener("progress", event => {
                if (event.lengthComputable) {
                    dotNetReference.invokeMethodAsync("ReportUploadProgress", event.loaded, event.total)
                        .catch(() => {});
                }
            });
            xhr.addEventListener("load", () => resolve({ status: xhr.status, body: xhr.responseText || "" }));
            xhr.addEventListener("error", () => reject(new Error("Network error during upload.")));
            xhr.addEventListener("abort", () => reject(new Error("Upload aborted.")));
            xhr.send(form);
        }),
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