#define _GNU_SOURCE
#include "agent_panel_ghostty.h"
#include <ghostty/vt.h>
#include <pty.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <sys/ioctl.h>
#include <poll.h>

struct ap_terminal {
    GhosttyTerminal terminal;
    GhosttyRenderState render;
    GhosttyRenderStateRowIterator iterator;
    GhosttyRenderStateRowCells cells;
    GhosttyKeyEncoder encoder;
    GhosttyKeyEvent key_event;
    int fd, cols, rows;
    pid_t pid;
};

static uint32_t rgb(GhosttyColorRgb color) {
    return 0xff000000u | ((uint32_t)color.r << 16) | ((uint32_t)color.g << 8) | color.b;
}

int ap_write(ap_terminal *t, const uint8_t *bytes, size_t length) {
    size_t written = 0;
    while (written < length) {
        ssize_t count = write(t->fd, bytes + written, length - written);
        if (count > 0) { written += (size_t)count; continue; }
        if (errno == EINTR) continue;
        if (errno == EAGAIN) {
            struct pollfd fd = { .fd = t->fd, .events = POLLOUT };
            if (poll(&fd, 1, 100) > 0) continue;
        }
        return -1;
    }
    return 0;
}

static void reply(GhosttyTerminal terminal, void *userdata, const uint8_t *bytes, size_t length) {
    (void)terminal;
    ap_write(userdata, bytes, length);
}

ap_terminal *ap_create(const char *directory, const char *command, int cols, int rows) {
    ap_terminal *t = calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->fd = -1; t->cols = cols; t->rows = rows;
    if (ghostty_terminal_new(NULL, &t->terminal, cols, rows) != GHOSTTY_SUCCESS ||
        ghostty_render_state_new(NULL, &t->render) != GHOSTTY_SUCCESS ||
        ghostty_render_state_row_iterator_new(NULL, &t->iterator) != GHOSTTY_SUCCESS ||
        ghostty_render_state_row_cells_new(NULL, &t->cells) != GHOSTTY_SUCCESS ||
        ghostty_key_encoder_new(NULL, &t->encoder) != GHOSTTY_SUCCESS ||
        ghostty_key_event_new(NULL, &t->key_event) != GHOSTTY_SUCCESS) {
        ap_destroy(t); return NULL;
    }
    ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_USERDATA, t);
    ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY, reply);
    GhosttyColorRgb fg = { .r = 228, .g = 228, .b = 231 };
    GhosttyColorRgb bg = { .r = 24, .g = 24, .b = 27 };
    ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &fg);
    ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &bg);
    /* Prepare environment before fork: the child only performs async-signal-safe calls. */
    extern char **environ;
    size_t count = 0;
    while (environ[count]) count++;
    char **environment = calloc(count + 4, sizeof(char *));
    if (!environment) { ap_destroy(t); return NULL; }
    size_t next = 0;
    for (size_t i = 0; i < count; i++)
        if (strncmp(environ[i], "TERM=", 5) && strncmp(environ[i], "COLORTERM=", 10) &&
            strncmp(environ[i], "TERM_PROGRAM=", 13)) environment[next++] = environ[i];
    environment[next++] = "TERM=xterm-256color";
    environment[next++] = "COLORTERM=truecolor";
    environment[next++] = "TERM_PROGRAM=agent-panel";
    char *const args[] = { "/bin/sh", "-lc", (char *)command, NULL };
    struct winsize size = { .ws_col = cols, .ws_row = rows };
    t->pid = forkpty(&t->fd, NULL, NULL, &size);
    if (t->pid == 0) {
        if (chdir(directory) != 0) _exit(126);
        execve("/bin/sh", args, environment);
        _exit(127);
    }
    free(environment);
    if (t->pid < 0) { ap_destroy(t); return NULL; }
    fcntl(t->fd, F_SETFL, O_NONBLOCK);
    return t;
}

int ap_pump(ap_terminal *t) {
    uint8_t buffer[16384];
    int changed = 0;
    /* Limit each tick so continuous output cannot starve the UI. */
    for (int i = 0; i < 16; i++) {
        ssize_t count = read(t->fd, buffer, sizeof(buffer));
        if (count > 0) { ghostty_terminal_vt_write(t->terminal, buffer, count); changed = 1; }
        else if (count == 0 || (count < 0 && errno == EIO)) return -1;
        else if (errno != EINTR) break;
    }
    return changed;
}

int ap_resize(ap_terminal *t, int cols, int rows) {
    if (cols == t->cols && rows == t->rows) return 0;
    if (ghostty_terminal_resize(t->terminal, cols, rows, 9, 19) != GHOSTTY_SUCCESS) return -1;
    t->cols = cols; t->rows = rows;
    struct winsize size = { .ws_col = cols, .ws_row = rows };
    return ioctl(t->fd, TIOCSWINSZ, &size);
}

int ap_key(ap_terminal *t, int key, int modifiers) {
    /* Stable bridge codes: 1..14 special keys, 100..125 A..Z, 200..211 F1..F12. */
    const GhosttyKey special[] = { GHOSTTY_KEY_UNIDENTIFIED, GHOSTTY_KEY_ENTER,
        GHOSTTY_KEY_BACKSPACE, GHOSTTY_KEY_TAB, GHOSTTY_KEY_ESCAPE,
        GHOSTTY_KEY_ARROW_UP, GHOSTTY_KEY_ARROW_DOWN, GHOSTTY_KEY_ARROW_RIGHT,
        GHOSTTY_KEY_ARROW_LEFT, GHOSTTY_KEY_HOME, GHOSTTY_KEY_END,
        GHOSTTY_KEY_DELETE, GHOSTTY_KEY_INSERT, GHOSTTY_KEY_PAGE_UP, GHOSTTY_KEY_PAGE_DOWN };
    GhosttyKey value = GHOSTTY_KEY_UNIDENTIFIED;
    if (key >= 1 && key <= 14) value = special[key];
    else if (key >= 100 && key <= 125) value = GHOSTTY_KEY_A + key - 100;
    else if (key >= 200 && key <= 211) value = GHOSTTY_KEY_F1 + key - 200;
    if (value == GHOSTTY_KEY_UNIDENTIFIED) return -1;
    ghostty_key_encoder_setopt_from_terminal(t->encoder, t->terminal);
    ghostty_key_event_set_action(t->key_event, GHOSTTY_KEY_ACTION_PRESS);
    ghostty_key_event_set_key(t->key_event, value);
    ghostty_key_event_set_mods(t->key_event, modifiers);
    char text[2] = { key >= 100 && key <= 125 ? 'a' + key - 100 : 0, 0 };
    ghostty_key_event_set_utf8(t->key_event, text, text[0] ? 1 : 0);
    ghostty_key_event_set_unshifted_codepoint(t->key_event, text[0]);
    char output[128]; size_t length = 0;
    if (ghostty_key_encoder_encode(t->encoder, t->key_event, output, sizeof(output), &length) != GHOSTTY_SUCCESS) return -1;
    return ap_write(t, (uint8_t *)output, length);
}

int ap_paste(ap_terminal *t, uint8_t *bytes, size_t length) {
    GhosttyTerminalModeConfig mode = { .mode = GHOSTTY_MODE_BRACKETED_PASTE };
    ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode);
    char *output = malloc(length + 32);
    if (!output) return -1;
    size_t written = 0;
    int result = -1;
    if (ghostty_paste_encode((char *)bytes, length, mode.value, output, length + 32, &written) == GHOSTTY_SUCCESS)
        result = ap_write(t, (uint8_t *)output, written);
    free(output);
    return result;
}

int ap_snapshot(ap_terminal *t, ap_cell *output, int capacity, int *cursor_x, int *cursor_y) {
    if (capacity < t->cols * t->rows) return -1;
    ghostty_render_state_update(t->render, t->terminal);
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &t->iterator);
    GhosttyColorRgb fg, bg;
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_COLOR_FOREGROUND, &fg);
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_COLOR_BACKGROUND, &bg);
    bool visible = false, has_cursor = false;
    uint16_t x = 0, y = 0;
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE, &visible);
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &has_cursor);
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &x);
    ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &y);
    *cursor_x = visible && has_cursor ? x : -1; *cursor_y = y;
    int index = 0;
    while (ghostty_render_state_row_iterator_next(t->iterator)) {
        ghostty_render_state_row_get(t->iterator, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &t->cells);
        while (ghostty_render_state_row_cells_next(t->cells) && index < capacity) {
            ap_cell *cell = &output[index++];
            memset(cell, 0, sizeof(*cell));
            GhosttyColorRgb cell_fg = fg, cell_bg = bg;
            ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &cell_fg);
            ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &cell_bg);
            GhosttyStyle style = GHOSTTY_INIT_SIZED(GhosttyStyle);
            ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style);
            cell->foreground = rgb(style.inverse ? cell_bg : cell_fg);
            cell->background = rgb(style.inverse ? cell_fg : cell_bg);
            cell->flags = (style.bold ? 1u : 0u) | (style.italic ? 2u : 0u) |
                (style.underline ? 4u : 0u) | (style.invisible ? 8u : 0u);
            GhosttyBuffer text = { .ptr = (uint8_t *)cell->text, .cap = sizeof(cell->text) - 1 };
            ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &text);
        }
    }
    return index;
}

void ap_scroll(ap_terminal *t, int lines) {
    GhosttyTerminalScrollViewport scroll = { .tag = GHOSTTY_SCROLL_VIEWPORT_DELTA, .value = { .delta = lines } };
    ghostty_terminal_scroll_viewport(t->terminal, scroll);
}

void ap_destroy(ap_terminal *t) {
    if (!t) return;
    if (t->fd >= 0) close(t->fd);
    if (t->pid > 0) {
        kill(-t->pid, SIGHUP);
        /* Reap synchronously, including a child that ignores SIGHUP. */
        if (waitpid(t->pid, NULL, WNOHANG) == 0) {
            kill(-t->pid, SIGKILL);
            while (waitpid(t->pid, NULL, 0) < 0 && errno == EINTR) {}
        }
    }
    if (t->cells) ghostty_render_state_row_cells_free(t->cells);
    if (t->key_event) ghostty_key_event_free(t->key_event);
    if (t->encoder) ghostty_key_encoder_free(t->encoder);
    if (t->iterator) ghostty_render_state_row_iterator_free(t->iterator);
    if (t->render) ghostty_render_state_free(t->render);
    if (t->terminal) ghostty_terminal_free(t->terminal);
    free(t);
}
