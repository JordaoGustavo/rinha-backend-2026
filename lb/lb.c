// Minimal TCP → Unix-domain-socket round-robin load balancer using
// epoll + splice(2). No HTTP parsing — pure byte forwarding, identical
// in semantics to haproxy's TCP mode but with a far thinner code path.
//
// Design:
//   - one listening TCP socket on :PORT
//   - on each new accepted client connection, picks the next backend
//     UDS in round-robin and connects to it
//   - per (client, backend) pair, two pipes splice bytes both directions
//     directly inside the kernel — zero copies into userspace
//   - edge-triggered, non-blocking I/O throughout
//
// Compile:
//   gcc -O2 -o lb lb.c
// Run:
//   ./lb [port]            (default port 9999)

#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define DEFAULT_PORT  9999
#define BACKLOG       4096
#define MAX_EVENTS    256
#define SPLICE_CHUNK  (64 * 1024)

static const char *BACKENDS[] = {
    "/run/sock/api1.sock",
    "/run/sock/api2.sock",
};
#define BACKEND_COUNT (sizeof(BACKENDS) / sizeof(BACKENDS[0]))

typedef struct conn {
    int client_fd, backend_fd;
    int c2b_pipe[2];   // client → backend
    int b2c_pipe[2];   // backend → client
    int c2b_pending;
    int b2c_pending;
    unsigned char client_half_closed;   // we received EOF from client
    unsigned char backend_half_closed;  // we received EOF from backend
} conn_t;

#define TAG_BIT     ((uintptr_t)1)
#define TAG_CLIENT  0
#define TAG_BACKEND 1

static int connect_backend(const char *path) {
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);
    if (connect(fd, (struct sockaddr*)&addr, sizeof(addr)) < 0
        && errno != EINPROGRESS) {
        close(fd);
        return -1;
    }
    return fd;
}

// Move bytes from `from` → pipe → `to`. Returns:
//   1 = made progress or no-op (try again later when epoll fires)
//   0 = `from` reached EOF (read half closed)
//  -1 = fatal error on either fd
static int pump(int from, int to, int pipe_fds[2], int *pending) {
    int got_eof = 0;
    while (*pending < SPLICE_CHUNK) {
        ssize_t n = splice(from, NULL, pipe_fds[1], NULL,
                           SPLICE_CHUNK - *pending,
                           SPLICE_F_MOVE | SPLICE_F_NONBLOCK);
        if (n > 0) {
            *pending += n;
        } else if (n == 0) {
            got_eof = 1;
            break;
        } else if (errno == EAGAIN || errno == EWOULDBLOCK) {
            break;
        } else if (errno == EINTR) {
            continue;
        } else {
            return -1;
        }
    }
    while (*pending > 0) {
        ssize_t n = splice(pipe_fds[0], NULL, to, NULL,
                           *pending,
                           SPLICE_F_MOVE | SPLICE_F_NONBLOCK);
        if (n > 0) {
            *pending -= n;
        } else if (errno == EAGAIN || errno == EWOULDBLOCK) {
            break;
        } else if (errno == EINTR) {
            continue;
        } else {
            return -1;
        }
    }
    return got_eof ? 0 : 1;
}

static void close_conn(int epoll_fd, conn_t *c) {
    epoll_ctl(epoll_fd, EPOLL_CTL_DEL, c->client_fd,  NULL);
    epoll_ctl(epoll_fd, EPOLL_CTL_DEL, c->backend_fd, NULL);
    close(c->client_fd);
    close(c->backend_fd);
    close(c->c2b_pipe[0]); close(c->c2b_pipe[1]);
    close(c->b2c_pipe[0]); close(c->b2c_pipe[1]);
    free(c);
}

int main(int argc, char **argv) {
    // Don't die when peer disappears mid-write.
    signal(SIGPIPE, SIG_IGN);

    int port = (argc > 1) ? atoi(argv[1]) : DEFAULT_PORT;
    if (port <= 0 || port > 65535) port = DEFAULT_PORT;

    int listen_fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (listen_fd < 0) { perror("socket"); return 1; }

    int one = 1;
    setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    setsockopt(listen_fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(one));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (bind(listen_fd, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        perror("bind"); return 1;
    }
    if (listen(listen_fd, BACKLOG) < 0) { perror("listen"); return 1; }

    int epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) { perror("epoll_create1"); return 1; }

    struct epoll_event ev = { .events = EPOLLIN, .data.ptr = NULL };
    epoll_ctl(epoll_fd, EPOLL_CTL_ADD, listen_fd, &ev);

    fprintf(stderr, "lb: listening :%d → %zu backends\n", port, BACKEND_COUNT);

    int next_backend = 0;
    struct epoll_event events[MAX_EVENTS];

    while (1) {
        int n = epoll_wait(epoll_fd, events, MAX_EVENTS, -1);
        if (n < 0) {
            if (errno == EINTR) continue;
            perror("epoll_wait"); break;
        }

        for (int i = 0; i < n; i++) {
            uintptr_t tag = (uintptr_t)events[i].data.ptr;

            if (tag == 0) {
                // listen_fd readable → drain accept queue
                while (1) {
                    int cfd = accept4(listen_fd, NULL, NULL,
                                      SOCK_NONBLOCK | SOCK_CLOEXEC);
                    if (cfd < 0) {
                        if (errno == EAGAIN || errno == EWOULDBLOCK) break;
                        if (errno == EINTR) continue;
                        break;
                    }
                    setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));

                    int idx = next_backend;
                    next_backend = (next_backend + 1) % BACKEND_COUNT;
                    int bfd = connect_backend(BACKENDS[idx]);
                    if (bfd < 0) { close(cfd); continue; }

                    conn_t *c = calloc(1, sizeof(conn_t));
                    if (!c) { close(cfd); close(bfd); continue; }
                    if (pipe2(c->c2b_pipe, O_NONBLOCK | O_CLOEXEC) < 0
                        || pipe2(c->b2c_pipe, O_NONBLOCK | O_CLOEXEC) < 0) {
                        free(c); close(cfd); close(bfd); continue;
                    }
                    c->client_fd  = cfd;
                    c->backend_fd = bfd;

                    struct epoll_event ce = {
                        .events = EPOLLIN | EPOLLOUT | EPOLLET | EPOLLRDHUP,
                        .data.ptr = (void*)((uintptr_t)c | TAG_CLIENT),
                    };
                    epoll_ctl(epoll_fd, EPOLL_CTL_ADD, cfd, &ce);

                    struct epoll_event be = {
                        .events = EPOLLIN | EPOLLOUT | EPOLLET | EPOLLRDHUP,
                        .data.ptr = (void*)((uintptr_t)c | TAG_BACKEND),
                    };
                    epoll_ctl(epoll_fd, EPOLL_CTL_ADD, bfd, &be);
                }
                continue;
            }

            conn_t *c = (conn_t*)(tag & ~TAG_BIT);
            uint32_t e = events[i].events;
            int dead = 0;

            // Pump both directions every wakeup. Edge-triggered epoll only
            // notifies on transitions, so we have to drain both sides.
            int r1 = pump(c->client_fd,  c->backend_fd, c->c2b_pipe, &c->c2b_pending);
            if (r1 < 0) {
                dead = 1;
            } else if (r1 == 0 && !c->client_half_closed) {
                c->client_half_closed = 1;
                shutdown(c->backend_fd, SHUT_WR);
            }

            int r2 = pump(c->backend_fd, c->client_fd, c->b2c_pipe, &c->b2c_pending);
            if (r2 < 0) {
                dead = 1;
            } else if (r2 == 0 && !c->backend_half_closed) {
                c->backend_half_closed = 1;
                shutdown(c->client_fd, SHUT_WR);
            }

            if (e & EPOLLERR) dead = 1;

            // Connection is fully done when both sides hit EOF and no more
            // bytes are queued in either pipe.
            if (dead || (c->client_half_closed && c->backend_half_closed
                         && c->c2b_pending == 0 && c->b2c_pending == 0)) {
                close_conn(epoll_fd, c);
            }
        }
    }

    close(listen_fd);
    close(epoll_fd);
    return 0;
}
