FROM ghcr.io/stop-cran/selenium/standalone-edge:114.0
USER 0
RUN apt update && \
    apt install -y --no-install-recommends ffmpeg \
	                                       libgstreamer1.0-0 \
                                           gstreamer1.0-plugins-base \
                                           gstreamer1.0-plugins-good \
                                           gstreamer1.0-plugins-bad \
                                           gstreamer1.0-plugins-ugly && \
    apt clean && \
	rm -rf /var/lib/apt/lists/*
USER 1200