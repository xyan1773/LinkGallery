#!/system/bin/sh
set -eu

seed="${1:?seed file name is required}"
directory=/sdcard/DCIM/LinkGalleryE2E
i=1
while [ "$i" -le 4999 ]; do
    cp "$directory/$seed" "$directory/scale_$i.jpg"
    i=$((i + 1))
done
