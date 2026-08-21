window.cdtDownload = {
    downloadTextFile: function (fileName, content, mimeType) {
        const blob = new Blob([content], { type: mimeType || "text/plain;charset=utf-8;" });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName || "download.txt";
        document.body.appendChild(anchor);
        anchor.click();
        document.body.removeChild(anchor);
        URL.revokeObjectURL(url);
    }
};
