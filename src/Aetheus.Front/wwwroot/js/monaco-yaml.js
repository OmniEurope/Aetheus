function completionRange(model, position) {
    const word = model.getWordUntilPosition(position);
    return {
        startLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: word.endColumn
    };
}

function completionItem(label, kind, insertText, range) {
    return { label, kind, insertText, range };
}

function parentKeyAt(model, position) {
    const lines = model.getValue().split('\n');
    for (let index = position.lineNumber - 2; index >= 0; index--) {
        const match = lines[index].match(/^(\w[\w_]*):/);
        if (match) return match[1];
    }
    return '';
}

function listValueCompletions(model, position, text, suggestions, range) {
    if (!/^\s*-\s*/.test(text)) return [];
    const names = {
        variable_libraries: suggestions.libraryNames,
        vaults: suggestions.vaultNames
    }[parentKeyAt(model, position)];
    if (!names) return [];
    return names.map(name => completionItem(
        name, monaco.languages.CompletionItemKind.Value, name, range));
}

function agentCompletions(text, suggestions, range) {
    if (!/^\s*agent:\s*/.test(text) || !suggestions.serverNames) return [];
    return suggestions.serverNames.map(name => completionItem(
        name, monaco.languages.CompletionItemKind.Value, name, range));
}

function variableCompletions(text, suggestions, range) {
    if (!/\$\([A-Za-z_]*$/.test(text) || !suggestions.variableKeys) return [];
    return suggestions.variableKeys.map(key => completionItem(
        key, monaco.languages.CompletionItemKind.Variable, key + ')', range));
}

function keywordCompletions(text, range) {
    if (!/^\w*$/.test(text.trim())) return [];
    const keywords = [
        'name', 'trigger', 'variables', 'variable_libraries',
        'vaults', 'stages', 'steps', 'shell', 'agent',
        'depends_on', 'timeout_seconds', 'type',
        'analysis_scope', 'analysis_preset', 'analysis_rules', 'analysis_grading',
        'key', 'minimum', 'maximum', 'operator', 'threshold',
        'metric', 'category', 'scanner', 'rule', 'severity',
        'new_findings_only', 'finding_score', 'branch', 'environment',
        'behavior', 'priority', 'enabled', 'minimum_grade',
        'required_domains', 'domain', 'direction',
        'a', 'b', 'c', 'd', 'e', 'required'
    ];
    return keywords.map(keyword => completionItem(
        keyword, monaco.languages.CompletionItemKind.Keyword, keyword + ': ', range));
}

function yamlCompletions(model, position, suggestions) {
    const text = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column
    });
    const range = completionRange(model, position);
    return {
        suggestions: [
            ...listValueCompletions(model, position, text, suggestions, range),
            ...agentCompletions(text, suggestions, range),
            ...variableCompletions(text, suggestions, range),
            ...keywordCompletions(text, range)
        ]
    };
}

window.monacoInterop = {
    _editors: {},
    _disposables: {},
    _diffEditors: {},

    init: async function (elementId, yamlContent, dotNetRef, isDark) {
        await this._ensureMonacoLoaded();

        const container = document.getElementById(elementId);
        if (!container) return;

        const editor = monaco.editor.create(container, {
            value: yamlContent || '',
            language: 'yaml',
            theme: isDark ? 'vs-dark' : 'vs',
            minimap: { enabled: false },
            automaticLayout: true,
            scrollBeyondLastLine: false,
            fontSize: 14,
            fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            tabSize: 2,
            insertSpaces: true,
            wordWrap: 'on',
            lineNumbers: 'on',
            renderLineHighlight: 'line',
            folding: true,
            bracketPairColorization: { enabled: true },
            padding: { top: 8, bottom: 8 },
            scrollbar: {
                verticalScrollbarSize: 10,
                horizontalScrollbarSize: 10,
                useShadows: false
            }
        });

        this._editors[elementId] = editor;
        this._disposables[elementId] = [];

        editor.onDidChangeModelContent(() => {
            const value = editor.getValue();
            dotNetRef.invokeMethodAsync('OnYamlChanged', value);
        });

        editor.onDidBlurEditorWidget(() => {
            dotNetRef.invokeMethodAsync('OnEditorBlur');
        });

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
            dotNetRef.invokeMethodAsync('OnEditorSave');
        });

        editor.onDidChangeCursorPosition((e) => {
            dotNetRef.invokeMethodAsync('OnCursorPositionChanged', e.position.lineNumber, e.position.column);
        });

        this._registerPipelineLinks(dotNetRef);
    },

    // `pipeline: <name>` lines become clickable links (Ctrl/Cmd-click) that open the referenced pipeline's
    // read-only definition peek in .NET. The provider is global (per-language); the opener routes the custom
    // `pipeline:` scheme back to the active editor's .NET ref.
    _registerPipelineLinks: function (dotNetRef) {
        this._pipelineLinkRef = dotNetRef;
        if (this._pipelineLinksRegistered) return;
        this._pipelineLinksRegistered = true;

        monaco.languages.registerLinkProvider('yaml', {
            provideLinks: function (model) {
                const links = [];
                const count = model.getLineCount();
                for (let i = 1; i <= count; i++) {
                    const text = model.getLineContent(i);
                    const m = text.match(/^(\s*(?:-\s*)?pipeline:\s*)(\S+)\s*$/);
                    if (m) {
                        const name = m[2].replace(/^["']|["']$/g, '');
                        const startCol = m[1].length + 1;
                        links.push({
                            range: new monaco.Range(i, startCol, i, startCol + m[2].length),
                            url: 'pipeline:' + name,
                            tooltip: 'Ctrl+Click: view pipeline definition'
                        });
                    }
                }
                return { links: links };
            }
        });

        if (monaco.editor.registerLinkOpener) {
            const self = this;
            monaco.editor.registerLinkOpener({
                open: function (uri) {
                    if (uri && uri.scheme === 'pipeline') {
                        self._pipelineLinkRef?.invokeMethodAsync('OnPipelineLinkActivated', uri.path);
                        return true;
                    }
                    return false;
                }
            });
        }
    },

    getValue: function (elementId) {
        const editor = this._editors[elementId];
        return editor ? editor.getValue() : '';
    },

    setValue: function (elementId, content) {
        const editor = this._editors[elementId];
        if (editor) {
            editor.setValue(content || '');
        }
    },

    setTheme: function (isDark) {
        if (typeof monaco !== 'undefined') {
            monaco.editor.setTheme(isDark ? 'vs-dark' : 'vs');
        }
    },

    setValidationErrors: function (elementId, errors) {
        const editor = this._editors[elementId];
        if (!editor) return;

        const model = editor.getModel();
        if (!model) return;

        const markers = (errors || []).map(e => ({
            severity: monaco.MarkerSeverity.Error,
            message: e.message,
            startLineNumber: e.startLine || 1,
            startColumn: e.startColumn || 1,
            endLineNumber: e.endLine || e.startLine || 1,
            endColumn: e.endColumn || 1000
        }));

        monaco.editor.setModelMarkers(model, 'yaml-validation', markers);
    },

    clearValidationErrors: function (elementId) {
        const editor = this._editors[elementId];
        if (!editor) return;

        const model = editor.getModel();
        if (model) {
            monaco.editor.setModelMarkers(model, 'yaml-validation', []);
        }
    },

    registerCompletionProvider: function (suggestions) {
        if (typeof monaco === 'undefined') return;

        const disposable = monaco.languages.registerCompletionItemProvider('yaml', {
            provideCompletionItems: (model, position) => yamlCompletions(model, position, suggestions)
        });

        this._globalCompletionDisposable = disposable;
    },

    updateSuggestions: function (suggestions) {
        if (this._globalCompletionDisposable) {
            this._globalCompletionDisposable.dispose();
        }
        this.registerCompletionProvider(suggestions);
    },

    dispose: function (elementId) {
        const editor = this._editors[elementId];
        if (editor) {
            editor.dispose();
            delete this._editors[elementId];
        }
        const disposables = this._disposables[elementId];
        if (disposables) {
            disposables.forEach(d => d.dispose());
            delete this._disposables[elementId];
        }
    },

    formatDocument: function (elementId) {
        const editor = this._editors[elementId];
        if (editor) {
            editor.getAction('editor.action.formatDocument')?.run();
        }
    },

    initDiffEditor: async function (elementId, originalContent, modifiedContent, isDark) {
        await this._ensureMonacoLoaded();

        const container = document.getElementById(elementId);
        if (!container) return;

        const originalModel = monaco.editor.createModel(originalContent || '', 'yaml');
        const modifiedModel = monaco.editor.createModel(modifiedContent || '', 'yaml');

        const diffEditor = monaco.editor.createDiffEditor(container, {
            theme: isDark ? 'vs-dark' : 'vs',
            automaticLayout: true,
            readOnly: true,
            renderSideBySide: true,
            scrollBeyondLastLine: false,
            fontSize: 14,
            fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            minimap: { enabled: false },
            scrollbar: {
                verticalScrollbarSize: 10,
                horizontalScrollbarSize: 10,
                useShadows: false
            }
        });

        diffEditor.setModel({ original: originalModel, modified: modifiedModel });
        this._diffEditors[elementId] = { editor: diffEditor, originalModel, modifiedModel };
    },

    updateDiffEditor: function (elementId, originalContent, modifiedContent) {
        const entry = this._diffEditors[elementId];
        if (!entry) return;

        entry.originalModel.setValue(originalContent || '');
        entry.modifiedModel.setValue(modifiedContent || '');
    },

    disposeDiffEditor: function (elementId) {
        const entry = this._diffEditors[elementId];
        if (entry) {
            entry.editor.setModel(null);
            entry.editor.dispose();
            entry.originalModel.dispose();
            entry.modifiedModel.dispose();
            delete this._diffEditors[elementId];
        }
    },

    initReadOnly: async function (elementId, content, language, isDark, lineNumber) {
        await this._ensureMonacoLoaded();

        const container = document.getElementById(elementId);
        if (!container) return;

        // Dispose previous instance if any
        if (this._editors[elementId]) {
            this._editors[elementId].dispose();
            delete this._editors[elementId];
        }

        const editor = monaco.editor.create(container, {
            value: content || '',
            language: language || 'plaintext',
            theme: isDark ? 'vs-dark' : 'vs',
            readOnly: true,
            minimap: { enabled: false },
            automaticLayout: true,
            scrollBeyondLastLine: false,
            fontSize: 14,
            fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            lineNumbers: 'on',
            renderLineHighlight: lineNumber ? 'line' : 'none',
            folding: true,
            domReadOnly: true,
            padding: { top: 8, bottom: 8 },
            scrollbar: {
                verticalScrollbarSize: 10,
                horizontalScrollbarSize: 10,
                useShadows: false
            }
        });

        this._editors[elementId] = editor;
        if (lineNumber) {
            const model = editor.getModel();
            const targetLine = Math.min(Math.max(1, Number(lineNumber)), model.getLineCount());
            editor.setSelection({
                startLineNumber: targetLine,
                startColumn: 1,
                endLineNumber: targetLine,
                endColumn: model.getLineMaxColumn(targetLine)
            });
            editor.revealLineInCenter(targetLine);
        }
    },

    disposeEditor: function (elementId) {
        const editor = this._editors[elementId];
        if (editor) {
            editor.dispose();
            delete this._editors[elementId];
        }
    },

    _monacoLoaded: false,
    _monacoLoading: null,

    _ensureMonacoLoaded: function () {
        if (this._monacoLoaded) return Promise.resolve();
        if (this._monacoLoading) return this._monacoLoading;

        this._monacoLoading = new Promise((resolve, reject) => {
            if (typeof monaco !== 'undefined') {
                this._monacoLoaded = true;
                resolve();
                return;
            }

            const script = document.createElement('script');
            script.src = '/lib/monaco-editor/min/vs/loader.js';
            script.onload = () => {
                require.config({
                    paths: { vs: '/lib/monaco-editor/min/vs' }
                });
                require(['vs/editor/editor.main'], () => {
                    this._monacoLoaded = true;
                    resolve();
                });
            };
            script.onerror = () => reject(new Error('Failed to load Monaco Editor'));
            document.head.appendChild(script);
        });

        return this._monacoLoading;
    }
};
