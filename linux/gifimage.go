package main

import (
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"image/gif"
	"io"
)

const gifMaxBytes = 2 * 1024 * 1024
const gifMaxSide = 512
const gifMaxFrames = 200
const gifMaxPixels = 40000000
const gifLimits = "GIF: up to 2 MiB, 512 x 512, 200 frames and 40 million total frame pixels"

func isGIF(data []byte) bool {
	return len(data) >= 6 && (string(data[:6]) == "GIF87a" || string(data[:6]) == "GIF89a")
}

// Inspect frame count and dimensions before DecodeAll can allocate the frames.
var errInvalidGIF = errors.New("invalid GIF")

func validateGIF(data []byte) error {
	if err := validateGIFContainer(data); err != nil {
		return fmt.Errorf("%w: %v", errInvalidGIF, err)
	}
	return nil
}

func validateGIFContainer(data []byte) error {
	if len(data) > gifMaxBytes {
		return fmt.Errorf("%s", gifLimits)
	}
	if !isGIF(data) || len(data) < 13 {
		return fmt.Errorf("invalid GIF")
	}
	width := int(binary.LittleEndian.Uint16(data[6:8]))
	height := int(binary.LittleEndian.Uint16(data[8:10]))
	if width > gifMaxSide || height > gifMaxSide {
		return fmt.Errorf("%s", gifLimits)
	}
	if width == 0 || height == 0 {
		return fmt.Errorf("invalid GIF dimensions")
	}
	r := bytes.NewReader(data[13:])
	skip := func(n int) error {
		if n > r.Len() {
			return io.ErrUnexpectedEOF
		}
		_, err := r.Seek(int64(n), io.SeekCurrent)
		return err
	}
	table := func(packed byte) error {
		if packed&128 != 0 {
			return skip(3 * (1 << ((packed & 7) + 1)))
		}
		return nil
	}
	blocks := func() error {
		for {
			n, err := r.ReadByte()
			if err != nil {
				return err
			}
			if n == 0 {
				return nil
			}
			if err := skip(int(n)); err != nil {
				return err
			}
		}
	}
	if err := table(data[10]); err != nil {
		return err
	}
	frames := 0
	for {
		block, err := r.ReadByte()
		if err != nil {
			return err
		}
		switch block {
		case 0x3b:
			if frames == 0 || r.Len() != 0 {
				return fmt.Errorf("invalid GIF trailer")
			}
			animation, err := gif.DecodeAll(bytes.NewReader(data))
			if err != nil {
				return fmt.Errorf("invalid GIF: %w", err)
			}
			if len(animation.Image) != frames {
				return fmt.Errorf("invalid GIF frames")
			}
			return nil
		case 0x21:
			if _, err := r.ReadByte(); err != nil {
				return err
			}
			if err := blocks(); err != nil {
				return err
			}
		case 0x2c:
			descriptor := make([]byte, 9)
			if _, err := io.ReadFull(r, descriptor); err != nil {
				return err
			}
			x := int(binary.LittleEndian.Uint16(descriptor[:2]))
			y := int(binary.LittleEndian.Uint16(descriptor[2:4]))
			w := int(binary.LittleEndian.Uint16(descriptor[4:6]))
			h := int(binary.LittleEndian.Uint16(descriptor[6:8]))
			if w == 0 || h == 0 || x+w > width || y+h > height {
				return fmt.Errorf("invalid GIF frame dimensions")
			}
			frames++
			if frames > gifMaxFrames || int64(width)*int64(height)*int64(frames) > gifMaxPixels {
				return fmt.Errorf("%s", gifLimits)
			}
			if err := table(descriptor[8]); err != nil {
				return err
			}
			if _, err := r.ReadByte(); err != nil {
				return err
			}
			if err := blocks(); err != nil {
				return err
			}
		default:
			return fmt.Errorf("invalid GIF block")
		}
	}
}
