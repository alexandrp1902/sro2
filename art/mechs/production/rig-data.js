window.MECH_RIG = {
  "version": 1,
  "id": "medium-autocannon-shield",
  "status": "validated-static-prototype",
  "animationReady": false,
  "cellSize": 512,
  "directions": [
    "N",
    "NE",
    "E",
    "SE",
    "S",
    "SW",
    "W",
    "NW"
  ],
  "groundPivot": [
    256,
    438
  ],
  "assets": {
    "chassis-medium-biped": {
      "source": "sources/chassis-medium-biped-8dir-v2.png",
      "frames": [
        [
          0,
          0,
          444,
          444
        ],
        [
          444,
          0,
          443,
          444
        ],
        [
          887,
          0,
          444,
          444
        ],
        [
          1331,
          0,
          443,
          444
        ],
        [
          0,
          444,
          444,
          443
        ],
        [
          444,
          444,
          443,
          443
        ],
        [
          887,
          444,
          444,
          443
        ],
        [
          1331,
          444,
          443,
          443
        ]
      ]
    },
    "body-medium": {
      "source": "sources/body-medium-8dir-v1.png",
      "frames": [
        [
          0,
          0,
          444,
          444
        ],
        [
          444,
          0,
          443,
          444
        ],
        [
          887,
          0,
          444,
          444
        ],
        [
          1331,
          0,
          443,
          444
        ],
        [
          0,
          444,
          444,
          443
        ],
        [
          444,
          444,
          443,
          443
        ],
        [
          887,
          444,
          444,
          443
        ],
        [
          1331,
          444,
          443,
          443
        ]
      ]
    },
    "arm-autocannon-right": {
      "source": "sources/arm-autocannon-right-8dir-v1.png",
      "frames": [
        [
          0,
          0,
          444,
          444
        ],
        [
          444,
          0,
          443,
          444
        ],
        [
          887,
          0,
          444,
          444
        ],
        [
          1331,
          0,
          443,
          444
        ],
        [
          0,
          444,
          444,
          443
        ],
        [
          444,
          444,
          443,
          443
        ],
        [
          887,
          444,
          444,
          443
        ],
        [
          1331,
          444,
          443,
          443
        ]
      ]
    },
    "shield-light-left": {
      "source": "sources/shield-light-left-8dir-v2.png",
      "frames": [
        [
          0,
          0,
          444,
          444
        ],
        [
          444,
          0,
          443,
          444
        ],
        [
          887,
          0,
          444,
          444
        ],
        [
          1331,
          0,
          443,
          444
        ],
        [
          0,
          444,
          444,
          443
        ],
        [
          444,
          444,
          443,
          443
        ],
        [
          887,
          444,
          444,
          443
        ],
        [
          1331,
          444,
          443,
          443
        ]
      ]
    }
  },
  "frames": {
    "N": {
      "transforms": {
        "chassis-medium-biped": {
          "x": 0,
          "y": 77.83200000000002,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": 81.40799999999996,
          "y": -68.088,
          "scale": 0.75,
          "z": 3,
          "enabled": true
        },
        "shield-light-left": {
          "x": -125.44,
          "y": -16.376000000000005,
          "scale": 0.65,
          "z": 4,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          256,
          263.944
        ],
        "leftShoulder": [
          163.83999999999997,
          176.392
        ],
        "rightShoulder": [
          352.768,
          176.392
        ],
        "muzzle": [
          414.2079999999999,
          72.71200000000002
        ]
      },
      "sourceAnchors": {
        "hip": [
          256,
          148.48
        ],
        "gunShoulder": [
          276.48,
          240.64
        ],
        "shieldShoulder": [
          307.2,
          158.72
        ]
      }
    },
    "NE": {
      "transforms": {
        "chassis-medium-biped": {
          "x": -4.608000000000004,
          "y": 73.22400000000002,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": 81.40800000000002,
          "y": -48.119999999999976,
          "scale": 0.75,
          "z": 3,
          "enabled": true
        },
        "shield-light-left": {
          "x": -102.91200000000003,
          "y": -50.68000000000001,
          "scale": 0.65,
          "z": 1,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          251.392,
          259.336
        ],
        "leftShoulder": [
          173.05599999999998,
          148.744
        ],
        "rightShoulder": [
          329.728,
          204.04000000000002
        ],
        "muzzle": [
          410.368,
          104.20000000000002
        ]
      },
      "sourceAnchors": {
        "hip": [
          256,
          148.48
        ],
        "gunShoulder": [
          245.76,
          250.88
        ],
        "shieldShoulder": [
          286.72,
          168.96
        ]
      }
    },
    "E": {
      "transforms": {
        "chassis-medium-biped": {
          "x": 13.311999999999983,
          "y": 68.61600000000001,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": 32.25599999999997,
          "y": -10.488000000000028,
          "scale": 0.75,
          "z": 3,
          "enabled": true
        },
        "shield-light-left": {
          "x": 16.127999999999986,
          "y": -44.792,
          "scale": 0.65,
          "z": 1,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          256,
          254.728
        ],
        "leftShoulder": [
          242.176,
          157.96
        ],
        "rightShoulder": [
          242.176,
          222.47199999999998
        ],
        "muzzle": [
          414.97599999999994,
          268.552
        ]
      },
      "sourceAnchors": {
        "hip": [
          235.52,
          148.48
        ],
        "gunShoulder": [
          194.56,
          225.28
        ],
        "shieldShoulder": [
          209.92,
          174.08
        ]
      }
    },
    "SE": {
      "transforms": {
        "chassis-medium-biped": {
          "x": 7.936000000000007,
          "y": 64.00800000000001,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": -29.951999999999998,
          "y": -31.99199999999999,
          "scale": 0.75,
          "z": 3,
          "enabled": true
        },
        "shield-light-left": {
          "x": 65.024,
          "y": -17.656000000000006,
          "scale": 0.65,
          "z": 1,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          260.608,
          250.12
        ],
        "leftShoulder": [
          334.336,
          171.784
        ],
        "rightShoulder": [
          168.44799999999998,
          208.64800000000002
        ],
        "muzzle": [
          256.768,
          327.688
        ]
      },
      "sourceAnchors": {
        "hip": [
          250.88,
          148.48
        ],
        "gunShoulder": [
          179.2,
          235.52
        ],
        "shieldShoulder": [
          276.48,
          153.6
        ]
      }
    },
    "S": {
      "transforms": {
        "chassis-medium-biped": {
          "x": 0,
          "y": 73.22400000000002,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": -42.24000000000001,
          "y": -33.52800000000002,
          "scale": 0.75,
          "z": 3,
          "enabled": true
        },
        "shield-light-left": {
          "x": 136.70399999999998,
          "y": -13.048000000000002,
          "scale": 0.65,
          "z": 4,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          256,
          259.336
        ],
        "leftShoulder": [
          352.768,
          176.392
        ],
        "rightShoulder": [
          163.83999999999997,
          176.392
        ],
        "muzzle": [
          160,
          314.63199999999995
        ]
      },
      "sourceAnchors": {
        "hip": [
          256,
          148.48
        ],
        "gunShoulder": [
          189.44,
          194.56
        ],
        "shieldShoulder": [
          194.56,
          153.6
        ]
      }
    },
    "SW": {
      "transforms": {
        "chassis-medium-biped": {
          "x": -7.935999999999979,
          "y": 73.22400000000002,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": -105.98400000000004,
          "y": -60.40799999999999,
          "scale": 0.75,
          "z": 1,
          "enabled": true
        },
        "shield-light-left": {
          "x": 102.912,
          "y": 17.928000000000026,
          "scale": 0.65,
          "z": 4,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          251.392,
          259.336
        ],
        "leftShoulder": [
          338.944,
          204.04000000000002
        ],
        "rightShoulder": [
          173.05599999999998,
          153.352
        ],
        "muzzle": [
          61.69599999999997,
          280.072
        ]
      },
      "sourceAnchors": {
        "hip": [
          261.12,
          148.48
        ],
        "gunShoulder": [
          286.72,
          199.68
        ],
        "shieldShoulder": [
          225.28,
          148.48
        ]
      }
    },
    "W": {
      "transforms": {
        "chassis-medium-biped": {
          "x": -6.656000000000006,
          "y": 79.88,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": 3.8400000000000034,
          "y": -76.536,
          "scale": 0.75,
          "z": 1,
          "enabled": true
        },
        "shield-light-left": {
          "x": -25.855999999999995,
          "y": 28.423999999999978,
          "scale": 0.65,
          "z": 4,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          256,
          259.336
        ],
        "leftShoulder": [
          246.784,
          217.86399999999998
        ],
        "rightShoulder": [
          256,
          148.744
        ],
        "muzzle": [
          136.96,
          171.784
        ]
      },
      "sourceAnchors": {
        "hip": [
          266.24,
          138.24
        ],
        "gunShoulder": [
          250.88,
          215.04
        ],
        "shieldShoulder": [
          281.6,
          153.6
        ]
      }
    },
    "NW": {
      "transforms": {
        "chassis-medium-biped": {
          "x": 1.2800000000000296,
          "y": 68.61600000000001,
          "scale": 0.65,
          "z": 0,
          "enabled": true
        },
        "body-medium": {
          "x": 0,
          "y": -75,
          "scale": 0.9,
          "z": 2,
          "enabled": true
        },
        "arm-autocannon-right": {
          "x": 42.24000000000001,
          "y": -80.376,
          "scale": 0.75,
          "z": 1,
          "enabled": true
        },
        "shield-light-left": {
          "x": -128.25600000000003,
          "y": -6.647999999999968,
          "scale": 0.65,
          "z": 4,
          "enabled": true
        }
      },
      "anchors": {
        "waist": [
          260.608,
          254.728
        ],
        "leftShoulder": [
          177.664,
          199.43200000000002
        ],
        "rightShoulder": [
          325.12,
          148.744
        ],
        "muzzle": [
          232.96,
          64.26399999999998
        ]
      },
      "sourceAnchors": {
        "hip": [
          261.12,
          148.48
        ],
        "gunShoulder": [
          291.84,
          220.16
        ],
        "shieldShoulder": [
          332.8,
          179.2
        ]
      }
    }
  }
};
