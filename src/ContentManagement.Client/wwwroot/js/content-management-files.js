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
        }
    };
})();